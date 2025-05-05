// Program.cs
using Docker.DotNet;
using Docker.DotNet.Models;
using DockerUpdateService.Models;
using System.Globalization;
using System.Net.Http.Headers;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

await Program.Main();

partial class Program
{
    // -------- Docker & Portainer clients --------
    private static DockerClient? dockerClient;
    private static HttpClient? portainerClient;

    // -------- Runtime state --------
    private static readonly HashSet<string> ignoredContainers = [];   // rolled-back or stack-managed
    private static readonly HashSet<string> excludeImages = [];   // from EXCLUDE_IMAGES
    private static readonly List<string> stackImages = []; // from Portainer stacks

    // -------- Scheduling --------
    private static string updateMode = "INTERVAL";
    private static string? updateInterval; // e.g. 10m
    private static string updateTime = "03:00";
    private static string updateDay = "1";

    // -------- Constants --------
    private static readonly TimeSpan backupRetention = TimeSpan.FromDays(5);
    private static readonly int containerCheckSeconds = 10;

    // -------- Entry point --------
    public static async Task Main()
    {
        Console.WriteLine("DockerUpdateService started.");

        LoadExcludeImages();
        LoadSchedulingConfig();
        InitDockerClient();
        InitPortainerClient();

        if (dockerClient == null)
        {
            Console.WriteLine("Could not create Docker client. Exiting.");
            return;
        }

        while (true)
        {
            try
            {
                await RemoveOldBackupsAndUnusedImages();
                if (portainerClient != null)
                    await CheckAndUpdatePortainerStacks(GetJsonOpts());
                await CheckAndUpdateContainers();
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Error during update cycle: {ex.Message}");
            }

            TimeSpan wait = CalculateNextWaitTime();
            Console.WriteLine($"Next check in {wait} ...");
            await Task.Delay(wait);
        }
    }

    // ========== ENVIRONMENT CONFIG ==========

    private static void LoadExcludeImages()
    {
        string? env = Environment.GetEnvironmentVariable("EXCLUDE_IMAGES");
        if (string.IsNullOrWhiteSpace(env)) return;

        foreach (string img in env.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            excludeImages.Add(img);

        Console.WriteLine($"Loaded {excludeImages.Count} excluded images.");
    }

    private static void LoadSchedulingConfig()
    {
        updateMode = Environment.GetEnvironmentVariable("UPDATE_MODE")?.ToUpperInvariant() ?? "INTERVAL";
        updateInterval = Environment.GetEnvironmentVariable("UPDATE_INTERVAL");
        updateTime = Environment.GetEnvironmentVariable("UPDATE_TIME") ?? "03:00";
        updateDay = Environment.GetEnvironmentVariable("UPDATE_DAY") ?? "1";

        Console.WriteLine($"Scheduling => MODE={updateMode} INTERVAL={updateInterval} TIME={updateTime} DAY={updateDay}");
    }

    // ========== CLIENT INITIALISATION ==========

    private static void InitDockerClient()
    {
        string? uri =
            RuntimeInformation.IsOSPlatform(OSPlatform.Windows)
                ? "npipe://./pipe/docker_engine"
                : (System.IO.File.Exists("/var/run/docker.sock") ? "unix:///var/run/docker.sock" : null);

        if (uri == null) return;

        dockerClient = new DockerClientConfiguration(new Uri(uri)).CreateClient();
        Console.WriteLine($"Connected to Docker: {uri}");
    }

    private static void InitPortainerClient()
    {
        string? url = Environment.GetEnvironmentVariable("PORTAINER_URL");
        string? key = Environment.GetEnvironmentVariable("PORTAINER_API_KEY");

        if (string.IsNullOrWhiteSpace(url) || string.IsNullOrWhiteSpace(key))
        {
            Console.WriteLine("Portainer integration disabled (URL or API key missing).");
            return;
        }

        var handler = new HttpClientHandler
        {
            ServerCertificateCustomValidationCallback = HttpClientHandler.DangerousAcceptAnyServerCertificateValidator
        };

        portainerClient = new HttpClient(handler) { BaseAddress = new Uri(url) };
        portainerClient.DefaultRequestHeaders.Add("X-API-Key", key);
        portainerClient.DefaultRequestHeaders.Accept.Add(
            new MediaTypeWithQualityHeaderValue("application/json"));
        Console.WriteLine($"Portainer client initialised for {url}");
    }

    // ========== SCHEDULING HELPERS ==========

    private static TimeSpan CalculateNextWaitTime() =>
        updateMode switch
        {
            "DAILY" => UntilNextDaily(updateTime),
            "WEEKLY" => UntilNextWeekly(updateDay, updateTime),
            "MONTHLY" => UntilNextMonthly(updateDay, updateTime),
            _ => ParseInterval(updateInterval ?? "10m")
        };

    private static TimeSpan ParseInterval(string s)
    {
        if (s.Length < 2) return TimeSpan.FromMinutes(10);
        char suffix = s[^1];
        if (!int.TryParse(s[..^1], out int n)) return TimeSpan.FromMinutes(10);
        return suffix switch
        {
            's' or 'S' => TimeSpan.FromSeconds(n),
            'm' or 'M' => TimeSpan.FromMinutes(n),
            'h' or 'H' => TimeSpan.FromHours(n),
            'd' or 'D' => TimeSpan.FromDays(n),
            _ => TimeSpan.FromMinutes(10)
        };
    }

    private static bool TryParseHHmm(string t, out int h, out int m)
    {
        h = 3; m = 0;
        var parts = t.Split(':', 2);
        return parts.Length == 2 &&
               int.TryParse(parts[0], out h) && h is >= 0 and <= 23 &&
               int.TryParse(parts[1], out m) && m is >= 0 and <= 59;
    }

    private static TimeSpan UntilNextDaily(string time)
    {
        if (!TryParseHHmm(time, out int h, out int m)) { h = 3; m = 0; }
        DateTime now = DateTime.Now;
        DateTime target = new(now.Year, now.Month, now.Day, h, m, 0);
        if (now >= target) target = target.AddDays(1);
        return target - now;
    }

    private static TimeSpan UntilNextWeekly(string day, string time)
    {
        if (!TryParseHHmm(time, out int h, out int m)) { h = 3; m = 0; }
        if (!Enum.TryParse(day, true, out DayOfWeek targetDay)) targetDay = DayOfWeek.Sunday;
        DateTime now = DateTime.Now;
        int daysDiff = ((int)targetDay - (int)now.DayOfWeek + 7) % 7;
        DateTime target = new DateTime(now.Year, now.Month, now.Day, h, m, 0).AddDays(daysDiff);
        if (now >= target) target = target.AddDays(7);
        return target - now;
    }

    private static TimeSpan UntilNextMonthly(string day, string time)
    {
        if (!TryParseHHmm(time, out int h, out int m)) { h = 3; m = 0; }
        _ = int.TryParse(day, out int d);
        d = Math.Clamp(d, 1, 28);
        DateTime now = DateTime.Now;
        DateTime target = new(now.Year, now.Month, d, h, m, 0);
        if (now >= target) target = target.AddMonths(1);
        return target - now;
    }

    private static JsonSerializerOptions GetJsonOpts()
    {
        return new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };
    }

    // ========== PORTAINER STACK UPDATE ==========

    /// <summary>
    /// Checks every Portainer stack, pulls the images referenced in its Compose
    /// file, redeploys the stack if one of those images has a newer ID, and then
    /// adds all containers from that stack to ignoredContainers.
    /// </summary>
    private static async Task CheckAndUpdatePortainerStacks(JsonSerializerOptions jsonOpts)
    {
        stackImages.Clear();
        if (portainerClient == null || dockerClient == null) return;

        Console.WriteLine("Querying Portainer stacks …");
        var listResp = await portainerClient.GetAsync("/api/stacks");
        if (!listResp.IsSuccessStatusCode)
        {
            Console.WriteLine($"Portainer stack list failed: {listResp.StatusCode}");
            return;
        }

        var stacks = await JsonSerializer
            .DeserializeAsync<List<PortainerStack>>(await listResp.Content.ReadAsStreamAsync()) ?? [];

        foreach (var stack in stacks)
        {
            Console.WriteLine($"Checking stack «{stack.Name}» (ID {stack.Id}) …");

            // 1) Download compose-file
            HttpResponseMessage fileResp = await portainerClient.GetAsync($"/api/stacks/{stack.Id}/file");
            fileResp.EnsureSuccessStatusCode();

            // Parse the JSON wrapper and take only the YAML string:
            string fileJson = await fileResp.Content.ReadAsStringAsync();
            string yaml = JsonSerializer.Deserialize<StackFileResponse>(fileJson)?.StackFileContent
                          ?? string.Empty;

            // 2) Parse all referenced images
            var images = ParseImagesFromYaml(yaml);

            bool updateNeeded = false;
            foreach (string img in images)
            {
                (string repro, string tag) = SplitImage(img);
                stackImages.Add($"{repro}");
                if (await ImageHasNewVersion(img))
                {
                    Console.WriteLine($"  Update available for {img}");
                    if (!excludeImages.Any(x => img.Contains(x)))
                        updateNeeded = true;
                }
            }
            if (!updateNeeded)
            {
                Console.WriteLine("  No updates for this stack.");
                continue;
            }

            // 3) Get current stack details so we can reuse its Env array
            var detailResp = await portainerClient.GetAsync($"/api/stacks/{stack.Id}");
            detailResp.EnsureSuccessStatusCode();

            StackEnv[] env = [];

            if (detailResp.IsSuccessStatusCode)
            {
                string envJson = await detailResp.Content.ReadAsStringAsync();
                env = [.. JsonSerializer.Deserialize<ComposeProject>(envJson)?.Env?.Select(e => new StackEnv(e.Name, e.Value)) ?? []];
            }

            var detail = await JsonSerializer.DeserializeAsync<PortainerStackWithFile>(
                             await detailResp.Content.ReadAsStreamAsync());

            var envArr = env ?? detail?.Env ?? [];

            // 4) Build payload and call the new update endpoint
            // ---- build the payload --------------------------------------------------
            var payload = new
            {
                stackFileContent = yaml,                 // plain YAML, NOT a JSON string
                env = envArr,
                prune = true
            };

            var body = new StringContent(
                JsonSerializer.Serialize(payload, jsonOpts),
                Encoding.UTF8,
                "application/json");

            // force Portainer to pull every image referenced in the compose file
            string url =
                $"/api/stacks/{stack.Id}"
              + $"?endpointId={stack.EndpointId}"
              + "&method=string"
              + "&pullImage=true" 
              + "&recreate=always";

            //Console.WriteLine("\n=== DEBUG ===");
            //Console.WriteLine(url);
            //Console.WriteLine(JsonSerializer.Serialize(payload,
            //    new JsonSerializerOptions { WriteIndented = true, PropertyNamingPolicy = JsonNamingPolicy.CamelCase }));
            //Console.WriteLine("=== END DEBUG ===\n");



            HttpResponseMessage upd = await portainerClient.PutAsync(url, body);



            if (!upd.IsSuccessStatusCode)
            {
                string err = await upd.Content.ReadAsStringAsync();
                Console.WriteLine($"  Stack update failed: {upd.StatusCode} – {err}");
                continue;
            }

            Console.WriteLine("  Stack redeployed successfully.");

            // 5) Add every container of that stack to the ignore list
            var related = await dockerClient.Containers.ListContainersAsync(
                new ContainersListParameters
                {
                    All = true,
                    Filters = new Dictionary<string, IDictionary<string, bool>>
                    {
                        ["label"] = new Dictionary<string, bool>
                        {
                            [$"com.docker.compose.project={stack.Name}"] = true
                        }
                    }
                });

            foreach (var ctr in related)
            {
                string? cname = ctr.Names.FirstOrDefault()?.TrimStart('/');
                if (cname != null && ignoredContainers.Add(cname))
                    Console.WriteLine($"    Container {cname} will be ignored in future single-container checks.");
            }
        }
    }

    /* ----------------------------------------------------------------- */
    /* Helper DTOs used only for JSON de-/serialization                  */

    private sealed record PortainerStack(int Id, string Name, int EndpointId);

    private sealed record StackEnv(string Name, string? Value);

    private sealed record StackFileResponse(string StackFileContent);

    private sealed record PortainerStackWithFile(
    int Id,
    string Name,
    int EndpointId,
    string StackFileContent,
    StackEnv[]? Env);



    private static List<string> ParseImagesFromYaml(string yaml)
    {
        List<string> list = [];
        foreach (string line in yaml.Split("\n", StringSplitOptions.RemoveEmptyEntries))
        {
            string t = line.Trim();
            if (t.StartsWith("image:", StringComparison.OrdinalIgnoreCase))
                list.Add(t["image:".Length..].Trim());
        }
        return list;
    }

    // dummy reuse of docker client logic
    private static async Task<bool> ImageHasNewVersion(string fullImage)
    {
        if (dockerClient == null) return false;

        (string repo, string tag) = SplitImage(fullImage);
        try
        {
            await dockerClient.Images.CreateImageAsync(new ImagesCreateParameters { FromImage = repo, Tag = tag }, null, new Progress<JSONMessage>());
        }
        catch { /* ignore pull errors */ }

        string? newId = (await dockerClient.Images.ListImagesAsync(new ImagesListParameters()))
            .FirstOrDefault(i => i.RepoTags?.Contains($"{repo}:{tag}") == true)?.ID;

        return newId != null && newId != await CurrentImageId(fullImage);
    }

    private static async Task<string?> CurrentImageId(string image)
    {
        if (dockerClient == null) return null;
        return (await dockerClient.Images.ListImagesAsync(new ImagesListParameters()))
            .FirstOrDefault(i => i.RepoTags?.Contains(image) == true)?.ID;
    }

    private static (string repo, string tag) SplitImage(string reference)
    {
        var match = ImageRegex().Match(reference);
        if (match.Success)
        {
            return (match.Groups["repo"].Value, match.Groups["tag"].Value.Replace("}", string.Empty));
        }
        else
        {
            // remove @sha256:… part
            int at = reference.IndexOf('@');
            if (at >= 0) reference = reference[..at];

            // repo[:tag] split
            int lastColon = reference.LastIndexOf(':');
            int lastSlash = reference.LastIndexOf('/');

            return (lastColon > lastSlash && lastColon >= 0)
                   ? (reference[..lastColon], reference[(lastColon + 1)..])
                   : (reference, "latest");
        }
    }

    // ========== PER-CONTAINER UPDATE (unchanged core logic, shortened) ==========

    private static async Task CheckAndUpdateContainers()
    {
        if (dockerClient == null) return;
        Console.WriteLine("Checking containers...");
        var containers = await dockerClient.Containers.ListContainersAsync(new ContainersListParameters { All = true });

        foreach (var ctr in containers)
        {
            string name = ctr.Names.FirstOrDefault()?.TrimStart('/') ?? ctr.ID;

            var details = await dockerClient.Containers.InspectContainerAsync(ctr.ID);

            // docker’s original reference (may include @digest)
            string originalRef = details.Config.Image ?? ctr.Image;

            // if the reference starts with "sha256:" we skip (nothing to pull)
            if (originalRef.StartsWith("sha256:", StringComparison.OrdinalIgnoreCase))
            {
                Console.WriteLine($"  Skip {name} – image was created from a digest only.");
                continue;
            }
            if(excludeImages.Any(x => originalRef.Contains(x) || x.Contains(name))) continue;
            if(ignoredContainers.Any(x => originalRef.Contains(x) || x.Contains(name))) continue;
            if(stackImages.Any(x => originalRef.Contains(x) || x.Contains(name))) continue;

            // cut off optional digest, then split repo:tag
            (string repo, string tag) = SplitImage(originalRef);
            string fullTag = $"{repo}:{tag}";

            Console.WriteLine($"  Checking {name} ({ctr.ImageID}) against {fullTag} ...");

            // pull & compare
            await dockerClient.Images.CreateImageAsync(new ImagesCreateParameters { FromImage = repo, Tag = tag }, null, new Progress<JSONMessage>());
            string? newId = (await dockerClient.Images.ListImagesAsync(new ImagesListParameters()))
                                .FirstOrDefault(i => i.RepoTags?.Contains(fullTag) == true)?.ID;

            if (newId == null || newId == ctr.ImageID) continue;

            Console.WriteLine($"Updating container {name} to new image {fullTag}");

            string backupTag = $"backup-{DateTime.UtcNow:yyyyMMddHHmmss}";
            try
            {
                await dockerClient.Images.TagImageAsync(ctr.ImageID, new ImageTagParameters { RepositoryName = repo, Tag = backupTag, Force = true });
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Backup failed for {name}: {ex.Message}");
                continue;
            }

            await RecreateContainer(repo, tag, ctr.ID, name, backupTag);
        }
    }

    private static async Task RecreateContainer(string repo, string tag, string oldId, string name, string backupTag)
    {
        if (dockerClient == null) return;
        var details = await dockerClient.Containers.InspectContainerAsync(oldId);

        await dockerClient.Containers.StopContainerAsync(oldId, new ContainerStopParameters());
        await dockerClient.Containers.RemoveContainerAsync(oldId, new ContainerRemoveParameters { Force = true });

        var create = new CreateContainerParameters
        {
            Name = details.Name.Trim('/'),
            Image = $"{repo}:{tag}",
            Env = details.Config.Env,
            Cmd = details.Config.Cmd,
            HostConfig = details.HostConfig,
            NetworkingConfig = new NetworkingConfig { EndpointsConfig = details.NetworkSettings.Networks?.ToDictionary(k => k.Key, v => v.Value) ?? [] }
        };
        var res = await dockerClient.Containers.CreateContainerAsync(create);
        await dockerClient.Containers.StartContainerAsync(res.ID, new ContainerStartParameters());

        bool ok = await CheckContainerHealth(name, containerCheckSeconds);
        if (ok) return;

        // rollback
        Console.WriteLine($"Rollback {name} ...");
        await dockerClient.Containers.StopContainerAsync(res.ID, new ContainerStopParameters());
        await dockerClient.Containers.RemoveContainerAsync(res.ID, new ContainerRemoveParameters { Force = true });

        var roll = new CreateContainerParameters
        {
            Name = create.Name,
            Image = $"{repo}:{backupTag}",
            Env = create.Env,
            Cmd = create.Cmd,
            HostConfig = create.HostConfig,
            NetworkingConfig = create.NetworkingConfig
        };
        var res2 = await dockerClient.Containers.CreateContainerAsync(roll);
        await dockerClient.Containers.StartContainerAsync(res2.ID, new ContainerStartParameters());
        ignoredContainers.Add(name);
    }

    private static async Task<bool> CheckContainerHealth(string name, int seconds)
    {
        if (dockerClient == null) return true;
        var ctr = (await dockerClient.Containers.ListContainersAsync(new ContainersListParameters { All = true }))
                  .FirstOrDefault(c => c.Names.Any(n => n.TrimStart('/') == name));
        if (ctr == null) return false;

        string id = ctr.ID;
        int elapsed = 0;
        while (elapsed < seconds * 1000)
        {
            var info = await dockerClient.Containers.InspectContainerAsync(id);
            if (!info.State.Running)
                return info.State.ExitCode == 0;
            await Task.Delay(2000);
            elapsed += 2000;
        }
        return true;
    }

    /// <summary>
    /// 1. Collects every image ID that is referenced by any container (running or stopped)
    ///    and by the Portainer stack scan (stackImages).
    /// 2. For every repository that *does* have a referenced tag, all other tags/digests
    ///    of that repo are removed – unless they are backup images younger than
    ///    <see cref="backupRetention"/>.
    /// 3. Images of completely unused repositories are kept (your requirement).
    /// </summary>
    private static async Task RemoveOldBackupsAndUnusedImages()
    {
        if (dockerClient == null) return;

        Console.WriteLine("Pruning backup images and unused tags …");

        DateTime now = DateTime.UtcNow;

        // ---- 1. Build a set of *used* ImageIDs ---------------------------------
        var usedImageIds = new HashSet<string>();
        var usedImageNames = new HashSet<string>();

        // a) every container (running or stopped)
        var containers = await dockerClient.Containers.ListContainersAsync(
                            new ContainersListParameters { All = true });
        foreach (var c in containers)
            usedImageIds.Add(c.ImageID);
        

        // ---- 2. Group all images by repository ----------------------------------
        var allImages = await dockerClient.Images.ListImagesAsync(new ImagesListParameters { All = true });
        var imagesByRepo = allImages
            .SelectMany(img => img.RepoDigests ?? [],
                        (img, name) => new { img, name })
            .GroupBy(x => string.Join(", ", x.name).Split("@", StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)[0]) // repository part
            .ToDictionary(g => g.Key, g => g.Select(x => (x.img, x.name)).ToList());

        // ---- 3. Iterate each repository -----------------------------------------
        foreach ((string repo, var list) in imagesByRepo)
        {
            bool repoInUse = list.Any(x => usedImageIds.Contains(x.img.ID));

            // If the repository is completely unused anywhere -> keep every tag
            if (!repoInUse) continue;
            else
                Console.WriteLine($"  Found {list.Count} tags for {repo} (used: {repoInUse})");

            foreach ((var img, string tag) in list)
            {
                bool tagInUse = usedImageIds.Contains(img.ID);
                bool isBackupTag = BackupRegex().IsMatch(tag);
                bool backupTooOld = false;

                if (isBackupTag)
                {
                    string ts = BackupRegex().Match(tag).Groups["stamp"].Value;
                    if (DateTime.TryParseExact(ts, "yyyyMMddHHmmss", CultureInfo.InvariantCulture,
                                               DateTimeStyles.None, out DateTime t))
                    {
                        backupTooOld = now - t > backupRetention;
                    }
                    else
                    {
                        // malformed timestamp = treat as old
                        backupTooOld = true;
                    }
                }

                // Removal rules:
                //  - not referenced by any container/stack
                //  - (if backup) older than retention OR malformed
                //  - (if normal tag) always safe to delete when unused
                if (!tagInUse && (!isBackupTag || backupTooOld))
                {
                    try
                    {
                        Console.WriteLine($"  Removing unused image {tag}");
                        await dockerClient.Images.DeleteImageAsync(
                            tag,
                            new ImageDeleteParameters { Force = true });
                    }
                    catch (Exception ex)
                    {
                        Console.WriteLine($"  Failed to delete {tag}: {ex.Message}");
                    }
                }
            }
        }
    }



    [GeneratedRegex(@"^(?<repo>.+):backup-(?<stamp>\d{14})$")]
    private static partial Regex BackupRegex();

    [GeneratedRegex(@"^(?<repo>[^:@\s]+(?:\/[^:@\s]+)*):(?:(\$\{[^}:]+:-)?(?<tag>[^@]+?))(?:@.*)?$")]
    private static partial Regex ImageRegex();
}
