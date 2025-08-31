using Docker.DotNet;
using Docker.DotNet.Models;
using DockerUpdateService.Options;
using DockerUpdateService.Util;
using Microsoft.Extensions.Logging;

namespace DockerUpdateService.Services;

public sealed class DockerEngineService(IDockerClient docker, UpdateOptions opts, ILogger<DockerEngineService> log)
{
    private readonly IDockerClient _docker = docker;
    private readonly UpdateOptions _opts = opts;
    private readonly ILogger<DockerEngineService> _log = log;

    public static (string repo, string tag) SplitImage(string reference)
    {
        var match = Patterns.ImageRegex().Match(reference);
        if (match.Success)
        {
            return (match.Groups["repo"].Value, match.Groups["tag"].Value.Replace("}", string.Empty));
        }
        else
        {
            int at = reference.IndexOf('@');
            if (at >= 0) reference = reference[..at];

            int lastColon = reference.LastIndexOf(':');
            int lastSlash = reference.LastIndexOf('/');

            return (lastColon > lastSlash && lastColon >= 0)
                   ? (reference[..lastColon], reference[(lastColon + 1)..])
                   : (reference, "latest");
        }
    }

    public async Task<string?> CurrentImageId(string image)
    {
        var imgs = await _docker.Images.ListImagesAsync(new ImagesListParameters());
        return imgs.FirstOrDefault(i => i.RepoTags?.Contains(image) == true)?.ID;
    }

    public async Task<bool> ImageHasNewVersion(string fullImage)
    {
        var (repo, tag) = SplitImage(fullImage);
        var imageKey = $"{repo}:{tag}";
        var candidates = CanonicalTagCandidates(repo, tag);

        // BEFORE pull
        var (oldId, oldDigests) = await InspectLocal(candidates);
        var oldDigest = FirstDigestForRepo(oldDigests, repo);

        // Pull with progress – record if daemon reports a newer download
        bool pulledNewer = false;
        var progress = new Progress<JSONMessage>(m =>
        {
            var s = m.Status?.ToLowerInvariant();
            if (s is null) return;
            if (s.Contains("downloaded newer image") || s.Contains("pulling fs layer") ||
                s.Contains("downloading") || s.Contains("extracting"))
            {
                pulledNewer = true;
            }
        });

        try
        {
            await _docker.Images.CreateImageAsync(
                new ImagesCreateParameters { FromImage = repo, Tag = tag },
                authConfig: null,
                progress: progress);
        }
        catch (Exception ex)
        {
            _log.LogDebug(ex, "Pull error for {Image}", imageKey);
            // keep going; we'll compare whatever we can
        }

        // AFTER pull
        var (newId, newDigests) = await InspectLocal(candidates);
        var newDigest = FirstDigestForRepo(newDigests, repo);

        _log.LogInformation("    Compare {Image}: ID {OldId} -> {NewId} ; Digest {OldDig} -> {NewDig}",
            imageKey, oldId, newId, oldDigest, newDigest);

        // No local image before but present after => treat as update available
        if (oldId is null && newId is not null) return true;

        // Primary signal: ID changed
        if (!string.Equals(oldId, newId, StringComparison.OrdinalIgnoreCase)) return true;

        // Secondary: digest changed
        if (!string.Equals(oldDigest, newDigest, StringComparison.OrdinalIgnoreCase)) return true;

        // Fallback: progress said we pulled something newer (rare ID race)
        return pulledNewer;
    }

    /// <summary>
    /// Try to resolve the local image by several canonical names and return (Id, RepoDigests).
    /// Works across Docker.DotNet versions without referencing specific response types.
    /// </summary>
    private async Task<(string? Id, IList<string>? RepoDigests)> InspectLocal(HashSet<string> candidates)
    {
        // 1) Try Inspect by name for each candidate
        foreach (var cand in candidates)
        {
            try
            {
                // The exact return type differs by package version; use 'var' and read properties dynamically.
                var info = await _docker.Images.InspectImageAsync(cand);
                if (info is not null)
                {
                    // both properties exist across versions
                    var id = (string?)info.GetType().GetProperty("ID")?.GetValue(info);
                    var digests = (IList<string>?)info.GetType().GetProperty("RepoDigests")?.GetValue(info);
                    if (!string.IsNullOrEmpty(id)) return (id, digests);
                }
            }
            catch
            {
                // 404 etc. – try next candidate
                _log.LogDebug("    Inspect failed for candidate {Cand}", cand);
            }
        }

        // 2) Fallback: scan RepoTags, then Inspect by ID to get digests
        var imgs = await _docker.Images.ListImagesAsync(new ImagesListParameters { All = true });
        foreach (var cand in candidates)
        {
            var id = imgs.FirstOrDefault(i => i.RepoTags?.Contains(cand) == true)?.ID;
            if (!string.IsNullOrEmpty(id))
            {
                try
                {
                    var info = await _docker.Images.InspectImageAsync(id);
                    var digests = (IList<string>?)info.GetType().GetProperty("RepoDigests")?.GetValue(info);
                    return (id, digests);
                }
                catch
                {
                    return (id, null);
                }
            }
        }
        return (null, null);
    }

    private static string? FirstDigestForRepo(IList<string>? repoDigests, string repo)
    {
        if (repoDigests is null) return null;

        // Exact match first (registry present)
        var hit = repoDigests.FirstOrDefault(d => d.StartsWith(repo + "@sha256:", StringComparison.OrdinalIgnoreCase));
        if (!string.IsNullOrEmpty(hit)) return hit;

        // Docker Hub canonical alternatives
        var alts = new List<string>();
        var firstSeg = repo.Split('/')[0];
        var hasRegistry = firstSeg.Contains('.') || firstSeg.Contains(':') || string.Equals(firstSeg, "localhost", StringComparison.OrdinalIgnoreCase);

        if (!hasRegistry)
        {
            if (!repo.Contains('/'))
            {
                alts.Add("docker.io/library/" + repo);
                alts.Add("index.docker.io/library/" + repo);
            }
            else
            {
                alts.Add("docker.io/" + repo);
                alts.Add("index.docker.io/" + repo);
            }
        }

        foreach (var alt in alts)
        {
            hit = repoDigests.FirstOrDefault(d => d.StartsWith(alt + "@sha256:", StringComparison.OrdinalIgnoreCase));
            if (!string.IsNullOrEmpty(hit)) return hit;
        }
        return null;
    }

    /// <summary>
    /// Return common canonical names the daemon may use for the same logical tag.
    /// </summary>
    private static HashSet<string> CanonicalTagCandidates(string repo, string tag)
    {
        var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        $"{repo}:{tag}"
    };

        var firstSeg = repo.Split('/')[0];
        var hasRegistry = firstSeg.Contains('.') || firstSeg.Contains(':') || string.Equals(firstSeg, "localhost", StringComparison.OrdinalIgnoreCase);

        if (!hasRegistry)
        {
            if (!repo.Contains('/'))
            {
                set.Add($"library/{repo}:{tag}");
                set.Add($"docker.io/library/{repo}:{tag}");
                set.Add($"index.docker.io/library/{repo}:{tag}");
            }
            else
            {
                set.Add($"docker.io/{repo}:{tag}");
                set.Add($"index.docker.io/{repo}:{tag}");
            }
        }
        return set;
    }

    /// <summary>
    /// Try to find the local image ID using multiple candidate names.
    /// Uses Inspect first (more exact), then falls back to scanning RepoTags.
    /// </summary>
    private async Task<string?> FindLocalImageId(HashSet<string> candidates)
    {
        // 1) Try inspect by name for each candidate
        foreach (var cand in candidates)
        {
            try
            {
                var info = await _docker.Images.InspectImageAsync(cand);
                _log.LogInformation("    Inspect found ID {Id} for candidate {Cand}", info.ID, cand);
                if (!string.IsNullOrEmpty(info?.ID)) return info.ID;
            }
            catch
            {
                _log.LogInformation("    Inspect failed for candidate {Cand}", cand);
                // 404 or other: try next candidate
            }
        }

        // 2) Fallback: scan RepoTags
        var imgs = await _docker.Images.ListImagesAsync(new ImagesListParameters { All = true });
        foreach (var cand in candidates)
        {
            var id = imgs.FirstOrDefault(i => i.RepoTags?.Contains(cand) == true)?.ID;
            if (!string.IsNullOrEmpty(id)) return id;
        }
        return null;
    }


    public async Task CheckAndUpdateContainers(HashSet<string> exclude, HashSet<string> ignoredContainers, List<string> stackImages, CancellationToken ct)
    {
        _log.LogInformation("Checking non-stack containers …");
        var containers = await _docker.Containers.ListContainersAsync(new ContainersListParameters { All = true }, ct);

        foreach (var ctr in containers)
        {
            ct.ThrowIfCancellationRequested();

            var name = ctr.Names.FirstOrDefault()?.TrimStart('/') ?? ctr.ID;
            var details = await _docker.Containers.InspectContainerAsync(ctr.ID, ct);
            var originalRef = details.Config.Image ?? ctr.Image;

            if (originalRef.StartsWith("sha256:", StringComparison.OrdinalIgnoreCase))
            {
                _log.LogInformation("  Skip {Name} – image was created from a digest only.", name);
                continue;
            }

            bool matches(HashSet<string> set) => set.Any(x =>
                originalRef.Contains(x, StringComparison.OrdinalIgnoreCase) || name.Contains(x, StringComparison.OrdinalIgnoreCase));

            if (matches(exclude)) continue;
            if (matches(ignoredContainers)) continue;
            if (stackImages.Any(x => originalRef.Contains(x, StringComparison.OrdinalIgnoreCase))) continue;

            var (repo, tag) = SplitImage(originalRef);
            var fullTag = $"{repo}:{tag}";
            _log.LogInformation("  Checking {Name} ({ImageId}) against {FullTag} …", name, ctr.ImageID, fullTag);

            await _docker.Images.CreateImageAsync(new ImagesCreateParameters { FromImage = repo, Tag = tag }, null, new Progress<JSONMessage>(), ct);
            var newId = (await _docker.Images.ListImagesAsync(new ImagesListParameters(), ct))
                            .FirstOrDefault(i => i.RepoTags?.Contains(fullTag) == true)?.ID;

            if (newId is null || newId == ctr.ImageID) continue;

            _log.LogInformation("  Updating container {Name} to newer image {FullTag}", name, fullTag);

            var backupTag = $"backup-{DateTime.UtcNow:yyyyMMddHHmmss}";
            try
            {
                await _docker.Images.TagImageAsync(ctr.ImageID, new ImageTagParameters { RepositoryName = repo, Tag = backupTag, Force = true }, ct);
            }
            catch (Exception ex)
            {
                _log.LogWarning(ex, "  Backup tag failed for {Name}. Skipping container.", name);
                continue;
            }

            await RecreateContainer(repo, tag, ctr.ID, name, backupTag, ct);
        }
    }

    private async Task RecreateContainer(string repo, string tag, string oldId, string name, string backupTag, CancellationToken ct)
    {
        var details = await _docker.Containers.InspectContainerAsync(oldId, ct);

        // Stop & remove old
        try { await _docker.Containers.StopContainerAsync(oldId, new ContainerStopParameters(), ct); } catch { }
        try { await _docker.Containers.RemoveContainerAsync(oldId, new ContainerRemoveParameters { Force = true }, ct); } catch { }

        // Prepare new container with the exact same settings
        var create = new CreateContainerParameters
        {
            Name = details.Name.Trim('/'),
            Image = $"{repo}:{tag}",
            Env = details.Config.Env,
            Cmd = details.Config.Cmd,
            Entrypoint = details.Config.Entrypoint,
            User = details.Config.User,
            WorkingDir = details.Config.WorkingDir,
            Labels = details.Config.Labels,
            HostConfig = details.HostConfig,
            NetworkingConfig = new NetworkingConfig
            {
                EndpointsConfig = details.NetworkSettings.Networks?.ToDictionary(k => k.Key, v => v.Value) ?? new Dictionary<string, EndpointSettings>()
            }
        };

        var res = await _docker.Containers.CreateContainerAsync(create, ct);
        await _docker.Containers.StartContainerAsync(res.ID, new ContainerStartParameters(), ct);

        var ok = await CheckContainerHealth(name, _opts.ContainerCheckSeconds, ct);
        if (ok) return;

        _log.LogWarning("  Health check failed; rolling back {Name} …", name);

        try
        {
            await _docker.Containers.StopContainerAsync(res.ID, new ContainerStopParameters(), ct);
            await _docker.Containers.RemoveContainerAsync(res.ID, new ContainerRemoveParameters { Force = true }, ct);
        }
        catch { }

        var roll = new CreateContainerParameters
        {
            Name = create.Name,
            Image = $"{repo}:{backupTag}",
            Env = create.Env,
            Cmd = create.Cmd,
            Entrypoint = create.Entrypoint,
            User = create.User,
            WorkingDir = create.WorkingDir,
            Labels = create.Labels,
            HostConfig = create.HostConfig,
            NetworkingConfig = create.NetworkingConfig
        };
        var res2 = await _docker.Containers.CreateContainerAsync(roll, ct);
        await _docker.Containers.StartContainerAsync(res2.ID, new ContainerStartParameters(), ct);
    }

    private async Task<bool> CheckContainerHealth(string name, int seconds, CancellationToken ct)
    {
        var ctr = (await _docker.Containers.ListContainersAsync(new ContainersListParameters { All = true }, ct))
                  .FirstOrDefault(c => c.Names.Any(n => n.TrimStart('/') == name));
        if (ctr is null) return false;

        var id = ctr.ID;
        var elapsed = 0;
        while (elapsed < seconds * 1000)
        {
            var info = await _docker.Containers.InspectContainerAsync(id, ct);
            if (!info.State.Running)
                return info.State.ExitCode == 0;
            await Task.Delay(2000, ct);
            elapsed += 2000;
        }
        return true;
    }

    public async Task PruneUnusedImages(HashSet<string> exclude, CancellationToken ct)
    {
        _log.LogInformation("Pruning unused images (excluding substrings: {Ex}) …", string.Join(", ", exclude));

        // Collect used image IDs
        var usedImageIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var containers = await _docker.Containers.ListContainersAsync(new ContainersListParameters { All = true }, ct);
        foreach (var c in containers)
            usedImageIds.Add(c.ImageID);

        var images = await _docker.Images.ListImagesAsync(new ImagesListParameters { All = true }, ct);

        foreach (var img in images)
        {
            if (usedImageIds.Contains(img.ID)) continue;

            var tags = img.RepoTags ?? new List<string>();

            // Keep excluded tags
            if (tags.Any(t => exclude.Any(ex => t.Contains(ex, StringComparison.OrdinalIgnoreCase))))
                continue;

            // Keep recent backups
            var isBackup = false; var tooOld = false;
            foreach (var t in tags)
            {
                var m = Patterns.BackupRegex().Match(t);
                if (m.Success)
                {
                    isBackup = true;
                    if (DateTime.TryParseExact(m.Groups["stamp"].Value, "yyyyMMddHHmmss",
                        System.Globalization.CultureInfo.InvariantCulture,
                        System.Globalization.DateTimeStyles.None, out var ts))
                    {
                        tooOld = (DateTime.UtcNow - ts) > TimeSpan.FromDays(_opts.BackupRetentionDays);
                    }
                    else tooOld = true;
                }
            }
            if (isBackup && !tooOld) continue;

            try
            {
                _log.LogInformation("  Deleting image {Id} ({Tags})", img.ID, string.Join(", ", tags));
                await _docker.Images.DeleteImageAsync(img.ID, new ImageDeleteParameters { Force = true }, ct);
            }
            catch (Exception ex)
            {
                _log.LogWarning(ex, "  Failed to delete image {Id}", img.ID);
            }
        }
    }
}