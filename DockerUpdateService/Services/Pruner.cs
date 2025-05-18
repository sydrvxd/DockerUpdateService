using Docker.DotNet;
using Docker.DotNet.Models;
using DockerUpdateService.Options;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using System.Globalization;
using System.Text.RegularExpressions;

namespace DockerUpdateService.Services;

internal sealed partial class Pruner(
    ILogger<Pruner> log,
    IDockerClient docker,
    IOptions<UpdateSettings> opts)
    : IPruner
{
    private readonly TimeSpan _retention = opts.Value.BackupRetention;

    public async Task PruneAsync(CancellationToken ct = default)
    {
        log.LogInformation("Pruning backup images and unused tags …");
        await RemoveOldBackupsAndUnusedImages(ct);
    }

    /// <summary>
    ///   1. Collects every image ID referenced by any container (running or stopped)
    ///      plus stack images handled elsewhere.<br/>
    ///   2. For each repository that *does* have a referenced tag, all other tags/digests
    ///      of that repo are removed – unless they are backup images younger than
    ///      <see cref="_retention"/>.<br/>
    ///   3. Entirely unused repositories are kept.
    /// </summary>
    private async Task RemoveOldBackupsAndUnusedImages(CancellationToken ct)
    {
        var now = DateTime.UtcNow;

        // ---- 1. build set of used image IDs ----------------------------------
        var used = new HashSet<string>();

        var containers = await docker.Containers.ListContainersAsync(
            new ContainersListParameters { All = true }, ct);

        foreach (var c in containers)
            used.Add(c.ImageID);

        // ---- 2. group all images by repository -------------------------------
        var allImages = await docker.Images.ListImagesAsync(
            new ImagesListParameters { All = true }, ct);

        var byRepo = allImages
            .SelectMany(img => img.RepoDigests ?? [],
                        (img, name) => new { img, name })
            .GroupBy(x => x.name.Split('@', 2)[0])
            .ToDictionary(g => g.Key,
                          g => g.Select(x => (x.img, x.name)).ToList());

        // ---- 3. iterate repositories -----------------------------------------
        foreach ((string repo, var list) in byRepo)
        {
            bool repoInUse = list.Any(x => used.Contains(x.img.ID));
            if (!repoInUse) continue;

            log.LogDebug(" {Repo}: {Count} tags (in‑use={RepoInUse})", repo, list.Count, repoInUse);

            foreach ((var img, string tag) in list)
            {
                bool tagInUse = used.Contains(img.ID);
                bool isBackup = BackupRegex().IsMatch(tag);
                bool backupTooOld = false;

                if (isBackup)
                {
                    var ts = BackupRegex().Match(tag).Groups["stamp"].Value;
                    if (DateTime.TryParseExact(ts, "yyyyMMddHHmmss", CultureInfo.InvariantCulture,
                                                DateTimeStyles.None, out var t))
                    {
                        backupTooOld = now - t > _retention;
                    }
                    else
                    {
                        backupTooOld = true; // malformed timestamp
                    }
                }

                if (!tagInUse && (!isBackup || backupTooOld))
                {
                    try
                    {
                        log.LogInformation("   Removing {Tag}", tag);
                        await docker.Images.DeleteImageAsync(tag,
                            new ImageDeleteParameters { Force = true }, ct);
                    }
                    catch (Exception ex)
                    {
                        log.LogWarning(ex, "   Failed deleting {Tag}", tag);
                    }
                }
            }
        }
    }

    [GeneratedRegex(@"^(?<repo>.+):backup-(?<stamp>\d{14})$")]
    private static partial Regex BackupRegex();
}
