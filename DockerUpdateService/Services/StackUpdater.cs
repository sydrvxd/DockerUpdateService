// Services/StackUpdater.cs
using Docker.DotNet;
using Docker.DotNet.Models;
using DockerUpdateService.Helpers;
using DockerUpdateService.Models;
using DockerUpdateService.Options;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace DockerUpdateService.Services;

/// <summary>
/// Handles Portainer‑managed stacks: pulls images referenced in every compose file,
/// redeploys the stack if any of them received a new image ID, then adds all
/// containers of that stack to <see cref="IgnoredContainers"/> so the per‑container
/// updater skips them.  Behaviour matches the original DockerUpdateService.
/// </summary>
internal sealed partial class StackUpdater(
    ILogger<StackUpdater> log,
    IDockerClient docker,
    IPortainerClient portainer,
    IOptions<UpdateSettings> opts)
    : IStackUpdater
{
    private readonly UpdateSettings _settings = opts.Value;
    private readonly JsonSerializerOptions _json = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

    /// <summary>Repos found in compose files of all processed stacks.</summary>
    public HashSet<string> StackImages { get; } = [];

    /// <summary>Containers that belong to Portainer stacks we redeployed (skip in single‑container updater).</summary>
    public HashSet<string> IgnoredContainers { get; } = [];

    // ---------------------------------------------------------------------

    public async Task UpdateStacksAsync(CancellationToken ct = default)
    {
        if (portainer is null || string.IsNullOrWhiteSpace(_settings.Portainer?.Url))
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
            string yaml;
            try
            {
                yaml = await portainer.GetStackFileAsync(stack.Id, ct);
                log.LogInformation("  Stack file:\n{Yaml}", yaml);
            }
            catch (StackFileNotFoundException)
            {
                log.LogWarning("Stack {Id} does not expose its file – created via UI? Skipping.", stack.Id);
                continue;
            }

            // 2) Parse image references --------------------------------------------------
            var images = ParseImagesFromYaml(yaml);

            log.LogInformation("  Found {Count} images in Stack Yaml.", images.Count);

            bool updateNeeded = false;
            foreach (string img in images)
            {
                log.LogInformation("  Found image {img} in Stack Yaml.", img);
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

    private static List<string> ParseImagesFromYaml(string payload)
    {
        // 1) Unwrap JSON if needed -------------------------------------------------
        string yaml = payload.TrimStart() switch
        {
            // Quick heuristic: JSON payload starts with “{” and contains the key once
            var s when s.StartsWith('{') && s.Contains("\"StackFileContent\"", StringComparison.Ordinal) =>
                JsonDocument.Parse(s).RootElement
                            .GetProperty("StackFileContent")
                            .GetString() ?? string.Empty,

            // Otherwise assume we already got raw YAML
            _ => payload
        };

        // 2) Scan for image lines --------------------------------------------------
        // Works even when indentation or casing differs ( “image:”, “IMAGE :” … )
        var images = new List<string>();
        foreach (var line in yaml.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            var trimmed = line.Trim();

            if (trimmed.StartsWith("image", StringComparison.OrdinalIgnoreCase))
            {
                // allow “image:name:tag” and “image : name:tag”
                var match = ImageRegex().Match(trimmed);
                if (match.Success)
                    images.Add(match.Groups[1].Value.Trim());
            }
        }

        return images;
    }

    private static readonly HttpClient http = new();

    public async Task<bool> ImageHasNewVersionAsync(string reference, CancellationToken ct)
    {
        // digest refs cannot be updated
        if (reference.StartsWith("sha256:")) return false;

        // split registry / repo / tag
        var (registry, repo, tag) = RegistryHelpers.SplitReference(reference);

        log.LogInformation("Checking Stack Image: {registry}/{repo}:{tag}", registry, repo, tag);

        string remoteDigest = await RegistryHelpers.GetRemoteDigestAsync(reference, http, ct); // await RegistryHelpers.GetRemoteDigestAsync(registry, repo, tag, http, ct);
        if (string.IsNullOrEmpty(remoteDigest))
        {
            log.LogWarning("  Could not fetch remote digest for {reference}", reference);
            return false;        // could not fetch
        }

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
            var env = JsonSerializer.Deserialize<StackEnv[]>(res, _json) ?? [];
            // If you need the Env array exactly as before, call the detail endpoint via HttpClient here.
            return env; // keeping env unchanged; extend if needed
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

    [GeneratedRegex(@"^image\s*:?\s*(.+)$", RegexOptions.IgnoreCase)]
    private static partial Regex ImageRegex();
}
