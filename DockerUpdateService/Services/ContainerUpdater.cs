// Services/ContainerUpdater.cs
using System.Globalization;
using System.Text.RegularExpressions;
using Docker.DotNet;
using Docker.DotNet.Models;
using DockerUpdateService.Helpers;
using DockerUpdateService.Options;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace DockerUpdateService.Services;

/// <summary>
/// Performs per‑container update checks (pull, recreate, rollback, backup tag, health probe)
/// matching the behaviour of the original DockerUpdateService implementation.
/// </summary>
internal sealed partial class ContainerUpdater(
    ILogger<ContainerUpdater> log,
    IDockerClient docker,
    IOptions<UpdateSettings> settings,
    IStackUpdater stackUpdater)
    : IContainerUpdater
{
    private readonly HashSet<string> _ignored = stackUpdater.IgnoredContainers;
    private readonly HashSet<string> _stackImages = stackUpdater.StackImages;

    private readonly int _healthTimeoutSeconds = 10;
    private readonly TimeSpan _backupRetention = settings.Value.BackupRetention;

    public async Task UpdateContainersAsync(CancellationToken ct = default)
    {
        log.LogInformation("Checking containers …");

        var containers = await docker.Containers.ListContainersAsync(
            new ContainersListParameters { All = true }, ct);

        foreach (var ctr in containers)
        {
            string name = ctr.Names.FirstOrDefault()?.TrimStart('/') ?? ctr.ID;
            (string repo, string tag) = ImageNameHelper.Split(ctr.Image);
            string fullTag = $"{repo}:{tag}";

            // ---- skip rules -----------------------------------------------------
            if (ctr.Image.StartsWith("sha256:", StringComparison.OrdinalIgnoreCase))
            {
                log.LogDebug("Skip {Name} – image was created from a digest only.", name);
                continue;
            }
            if (settings.Value.ExcludeImages.Any(x => ctr.Image.Contains(x)))
                continue;
            if (_ignored.Contains(name) || _stackImages.Contains(repo))
                continue;

            // ---- pull & compare -------------------------------------------------
            await docker.Images.CreateImageAsync(
                new ImagesCreateParameters { FromImage = repo, Tag = tag },
                null,
                new Progress<JSONMessage>(),
                ct);

            string? newId =
                (await docker.Images.ListImagesAsync(new ImagesListParameters(), ct))
                .FirstOrDefault(i => i.RepoTags?.Contains(fullTag) == true)?.ID;

            if (newId == null || newId == ctr.ImageID) continue;

            log.LogInformation("Updating {Name} to new image {Tag}", name, fullTag);

            string backupTag = $"backup-{DateTime.UtcNow:yyyyMMddHHmmss}";
            await TagAsBackupAsync(ctr.ImageID, repo, backupTag, name, ct);
            await RecreateContainerAsync(repo, tag, ctr.ID, name, backupTag, ct);
        }
    }

    // ------------------------------------------------------------------------

    private async Task TagAsBackupAsync(
        string imageId, string repo, string backupTag, string name, CancellationToken ct)
    {
        try
        {
            await docker.Images.TagImageAsync(
                imageId,
                new ImageTagParameters { RepositoryName = repo, Tag = backupTag, Force = true },
                ct);
        }
        catch (Exception ex)
        {
            log.LogWarning(ex, "Backup tagging failed for {Name}", name);
            throw;
        }
    }

    private async Task RecreateContainerAsync(
        string repo, string tag, string oldId, string name, string backupTag, CancellationToken ct)
    {
        var details = await docker.Containers.InspectContainerAsync(oldId, ct);

        // stop + remove old
        await docker.Containers.StopContainerAsync(oldId, new ContainerStopParameters(), ct);
        await docker.Containers.RemoveContainerAsync(
            oldId, new ContainerRemoveParameters { Force = true }, ct);

        // create new
        var create = new CreateContainerParameters
        {
            Name = details.Name.Trim('/'),
            Image = $"{repo}:{tag}",
            Env = details.Config.Env,
            Cmd = details.Config.Cmd,
            HostConfig = details.HostConfig,
            NetworkingConfig = new NetworkingConfig
            {
                EndpointsConfig = details.NetworkSettings.Networks?
                    .ToDictionary(k => k.Key, v => v.Value) ?? []
            }
        };
        var res = await docker.Containers.CreateContainerAsync(create, ct);
        await docker.Containers.StartContainerAsync(res.ID, new ContainerStartParameters(), ct);

        bool ok = await CheckContainerHealthAsync(create.Name, ct);
        if (ok) return;

        // rollback ------------------------------------------------------------
        log.LogWarning("Health‑check failed, rolling back {Name}", name);

        await docker.Containers.StopContainerAsync(res.ID, new ContainerStopParameters(), ct);
        await docker.Containers.RemoveContainerAsync(res.ID, new ContainerRemoveParameters { Force = true }, ct);

        var roll = new CreateContainerParameters
        {
            Name = create.Name,
            Image = $"{repo}:{backupTag}",
            Env = create.Env,
            Cmd = create.Cmd,
            HostConfig = create.HostConfig,
            NetworkingConfig = create.NetworkingConfig
        };
        var res2 = await docker.Containers.CreateContainerAsync(roll, ct);
        await docker.Containers.StartContainerAsync(res2.ID, new ContainerStartParameters(), ct);

        _ignored.Add(name); // skip on next iterations
    }

    private async Task<bool> CheckContainerHealthAsync(string name, CancellationToken ct)
    {
        var ctr = (await docker.Containers.ListContainersAsync(
                       new ContainersListParameters { All = true }, ct))
                  .FirstOrDefault(c => c.Names.Any(n => n.TrimStart('/') == name));
        if (ctr == null) return false;

        string id = ctr.ID;
        int elapsed = 0;
        while (elapsed < _healthTimeoutSeconds * 1_000)
        {
            var info = await docker.Containers.InspectContainerAsync(id, ct);
            if (!info.State.Running)
                return info.State.ExitCode == 0;

            await Task.Delay(2_000, ct);
            elapsed += 2_000;
        }
        return true; // no health‑check → assume OK
    }

    // ------------------------------------------------------------------------
    // Regex used by Pruner to match backup tags
    [GeneratedRegex(@"^.+:backup-(?<stamp>\d{14})$")]
    private static partial Regex BackupRegex();
}
