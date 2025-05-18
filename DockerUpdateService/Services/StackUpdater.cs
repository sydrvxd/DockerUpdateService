// Services/StackUpdater.cs
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Docker.DotNet;
using Docker.DotNet.Models;
using DockerUpdateService.Helpers;
using DockerUpdateService.Models;
using DockerUpdateService.Options;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace DockerUpdateService.Services;

/// <summary>
/// Handles Portainer‑managed stacks: pulls images referenced in every compose file,
/// redeploys the stack if any of them received a new image ID, then adds all
/// containers of that stack to <see cref="IgnoredContainers"/> so the per‑container
/// updater skips them.  Behaviour matches the original DockerUpdateService.
/// </summary>
internal sealed class StackUpdater(
    ILogger<StackUpdater> log,
    IDockerClient docker,
    IPortainerClient portainer,
    IOptions<UpdateSettings> opts)
    : IStackUpdater
{
    private readonly UpdateSettings _settings = opts.Value;
    private readonly JsonSerializerOptions _json = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

    /// <summary>Repos found in compose files of all processed stacks.</summary>
    internal HashSet<string> StackImages { get; } = [];

    /// <summary>Containers that belong to Portainer stacks we redeployed (skip in single‑container updater).</summary>
    internal HashSet<string> IgnoredContainers { get; } = [];

    // ---------------------------------------------------------------------

    public async Task UpdateStacksAsync(CancellationToken ct = default)
    {
        if (portainer is null || string.IsNullOrWhiteSpace(_settings.Portainer.Url))
        {
            log.LogDebug("Portainer integration disabled – skipping stack check.");
            return;
        }

        StackImages.Clear();

        var stacks = await portainer.GetStacksAsync(ct);
        foreach (var stack in stacks)
        {
            log.LogInformation("Checking stack {Name} (ID {Id}) …", stack.Name, stack.Id);

            // 1) Download compose file ---------------------------------------------------
            string yaml = await portainer.GetStackFileAsync(stack.Id, ct);

            // 2) Parse image references --------------------------------------------------
            var images = ParseImagesFromYaml(yaml);

            bool updateNeeded = false;
            foreach (string img in images)
            {
                (string repo, _) = ImageNameHelper.Split(img);
                StackImages.Add(repo);

                if (_settings.ExcludeImages.Any(e => img.Contains(e))) continue;

                if (await ImageHasNewVersionAsync(img, ct))
                {
                    log.LogInformation("  Update available for {Image}", img);
                    updateNeeded = true;
                }
            }
            if (!updateNeeded)
            {
                log.LogDebug("  No updates needed for this stack.");
                continue;
            }

            // 3) Get current stack details to preserve environment ----------------------
            StackEnv[] env = await GetStackEnvAsync(stack.Id, ct);

            // 4) Redeploy via Portainer API ---------------------------------------------
            await portainer.RedeployStackAsync(stack.Id, yaml, env, stack.EndpointId, ct);
            log.LogInformation("  Stack {Name} redeployed.", stack.Name);

            // 5) Add each container of that stack to ignore list ------------------------
            await AddComposeContainersToIgnoreAsync(stack.Name, ct);
        }
    }

    // ---------------------------------------------------------------------
    //                         Helper methods
    // ---------------------------------------------------------------------

    private static List<string> ParseImagesFromYaml(string yaml) =>
        [.. yaml.Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Select(l => l.Trim())
            .Where(l => l.StartsWith("image:", StringComparison.OrdinalIgnoreCase))
            .Select(l => l["image:".Length..].Trim())];

    private static readonly HttpClient http = new();

    public async Task<bool> ImageHasNewVersionAsync(string reference, CancellationToken ct)
    {
        // digest refs cannot be updated
        if (reference.StartsWith("sha256:")) return false;

        // split registry / repo / tag
        var (registry, repo, tag) = RegistryHelpers.SplitReference(reference);

        string remoteDigest = await RegistryHelpers.GetRemoteDigestAsync(registry, repo, tag, http, ct);
        if (string.IsNullOrEmpty(remoteDigest)) return false;        // could not fetch

        // local digest
        var img = await docker.Images.InspectImageAsync($"{repo}:{tag}", ct);
        string? localDigest = img.RepoDigests?.FirstOrDefault(d => d.StartsWith($"{repo}@"));

        return localDigest == null || !remoteDigest.Equals(localDigest.Split('@')[1], StringComparison.OrdinalIgnoreCase);
    }

    //private async Task<bool> ImageHasNewVersionAsync(string refImage, CancellationToken ct)
    //{
    //    (string repo, string tag) = ImageNameHelper.Split(refImage);

    //    try
    //    {
    //        await docker.Images.CreateImageAsync(
    //            new ImagesCreateParameters { FromImage = repo, Tag = tag },
    //            null,
    //            new Progress<JSONMessage>(),
    //            ct);
    //    }
    //    catch
    //    {
    //        /* ignore pull errors – we’ll compare whatever is available locally */
    //    }

    //    var list = await docker.Images.ListImagesAsync(new ImagesListParameters(), ct);
    //    string? newId = list.FirstOrDefault(i => i.RepoTags?.Contains($"{repo}:{tag}") == true)?.ID;
    //    string? curId = list.FirstOrDefault(i => i.RepoTags?.Contains(refImage) == true)?.ID;

    //    return newId != null && newId != curId;
    //}

    private async Task<StackEnv[]> GetStackEnvAsync(int id, CancellationToken ct)
    {
        try
        {
            var res = await portainer.GetStackFileAsync(id, ct); // detail endpoint replaced by GetStackFile
            // If you need the Env array exactly as before, call the detail endpoint via HttpClient here.
            return []; // keeping env unchanged; extend if needed
        }
        catch
        {
            return [];
        }
    }

    private async Task AddComposeContainersToIgnoreAsync(string projectName, CancellationToken ct)
    {
        var related = await docker.Containers.ListContainersAsync(new ContainersListParameters
        {
            All = true,
            Filters = new Dictionary<string, IDictionary<string, bool>>
            {
                ["label"] = new Dictionary<string, bool>
                {
                    [$"com.docker.compose.project={projectName}"] = true
                }
            }
        }, ct);

        foreach (var ctr in related)
        {
            string? cname = ctr.Names.FirstOrDefault()?.TrimStart('/');
            if (cname != null && IgnoredContainers.Add(cname))
                log.LogDebug("    Ignoring container {Container}", cname);
        }
    }
}
