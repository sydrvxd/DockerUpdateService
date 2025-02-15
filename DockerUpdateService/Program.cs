
using Docker.DotNet;
using Docker.DotNet.Models;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;


await Main();


partial class Program
{
    private static DockerClient? dockerClient;

    // Existing fields (ignored containers, etc.)...
    private static readonly HashSet<string> ignoredContainers = [];
    private static readonly HashSet<string> excludeImages = [];

    // Scheduling-related environment variables
    private static string? updateMode;
    private static string? updateInterval; // e.g. "10m", "1h", "30s"
    private static string? updateTime;     // e.g. "03:00" for 3 AM
    private static string? updateDay;      // e.g. "SUNDAY" or "1"

    // Some existing constants
    private static readonly TimeSpan backupRetention = TimeSpan.FromDays(30);
    private static readonly int containerCheckSeconds = 10;

    static async Task Main()
    {
        Console.WriteLine("Docker Auto-Updater with Backup, Rollback, and Exclude List has started...");

        // 1) Load exclude list from environment variable
        LoadExcludeImages();

        // 2) Detect Docker environment (Windows or Linux/Mac)
        string? dockerUri = DetectDockerEnvironment();
        if (dockerUri == null)
        {
            Console.WriteLine("No Docker environment detected. Exiting.");
            return;
        }

        dockerClient = new DockerClientConfiguration(new Uri(dockerUri)).CreateClient();
        Console.WriteLine($"Connected to Docker API at: {dockerUri}");

        // 3) Main loop: run updates, then wait for the next scheduled time
        while (true)
        {
            try
            {
                await RemoveOldBackups();
                await CheckAndUpdateContainers();
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Error during update cycle: {ex.Message}");
            }

            // 4) Calculate how long to wait until the next check
            TimeSpan waitTime = CalculateNextWaitTime();
            Console.WriteLine($"Next check in {waitTime}...");
            await Task.Delay(waitTime);
        }
    }

    /// <summary>
    /// Calculates how long to wait until the next update check, based on UPDATE_MODE and related vars.
    /// </summary>
    private static TimeSpan CalculateNextWaitTime()
    {
        switch (updateMode)
        {
            case "INTERVAL":
                return ParseInterval(updateInterval ?? "10m"); // default to 10 minutes if not set

            case "DAILY":
                return GetTimeUntilNextDaily(updateTime ?? "03:00");

            case "WEEKLY":
                return GetTimeUntilNextWeekly(updateDay ?? "SUNDAY", updateTime ?? "03:00");

            case "MONTHLY":
                return GetTimeUntilNextMonthly(updateDay ?? "1", updateTime ?? "03:00");

            default:
                Console.WriteLine($"Unknown UPDATE_MODE '{updateMode}', defaulting to INTERVAL 10m.");
                return TimeSpan.FromMinutes(10);
        }
    }

    /// <summary>
    /// Parses an interval string like "10m", "1h", "30s" into a TimeSpan.
    /// Defaults to 10 minutes if invalid.
    /// </summary>
    private static TimeSpan ParseInterval(string interval)
    {
        // Very simple parser: check last char for s/m/h/d
        // You can make this more robust as needed
        if (string.IsNullOrWhiteSpace(interval)) return TimeSpan.FromMinutes(10);

        char last = interval[^1];
        if (!char.IsDigit(last))
        {
            // e.g. "10m" => number is "10", suffix is 'm'
            string numPart = interval[..^1];
            if (int.TryParse(numPart, out int val))
            {
                switch (char.ToLowerInvariant(last))
                {
                    case 's': return TimeSpan.FromSeconds(val);
                    case 'm': return TimeSpan.FromMinutes(val);
                    case 'h': return TimeSpan.FromHours(val);
                    case 'd': return TimeSpan.FromDays(val);
                }
            }
        }
        // fallback
        Console.WriteLine($"Cannot parse interval '{interval}', defaulting to 10m.");
        return TimeSpan.FromMinutes(10);
    }

    /// <summary>
    /// Calculates how long until the next occurrence of HH:mm today or tomorrow (daily).
    /// E.g. if it's 2:50 AM and timeString=03:00 => 10 minutes
    /// If it's 4:00 AM => 23 hours until tomorrow 3 AM
    /// </summary>
    private static TimeSpan GetTimeUntilNextDaily(string timeString)
    {
        if (!TryParseHourMinute(timeString, out int hh, out int mm))
        {
            // fallback to 03:00
            hh = 3; mm = 0;
        }
        DateTime now = DateTime.Now;
        DateTime todayTarget = new(now.Year, now.Month, now.Day, hh, mm, 0);

        if (now > todayTarget)
        {
            // time already passed today, schedule for tomorrow
            todayTarget = todayTarget.AddDays(1);
        }
        return todayTarget - now;
    }

    /// <summary>
    /// Weekly schedule: next occurrence of 'dayOfWeek' at HH:mm.
    /// e.g. dayString="SUNDAY", timeString="03:00"
    /// </summary>
    private static TimeSpan GetTimeUntilNextWeekly(string dayString, string timeString)
    {
        if (!TryParseHourMinute(timeString, out int hh, out int mm))
        {
            hh = 3; mm = 0;
        }
        if (!Enum.TryParse(dayString, true, out DayOfWeek targetDay))
        {
            // default to Sunday
            targetDay = DayOfWeek.Sunday;
        }

        DateTime now = DateTime.Now;
        // find next target day at hh:mm
        // e.g. if today is Tuesday and target is Sunday => 5 days away
        // if day is same but time is in the past => 7 days away
        int currentDay = (int)now.DayOfWeek;
        int desiredDay = (int)targetDay;

        int daysUntil = desiredDay - currentDay;
        if (daysUntil < 0)
        {
            daysUntil += 7; // wrap around
        }

        DateTime nextTarget = new DateTime(now.Year, now.Month, now.Day, hh, mm, 0).AddDays(daysUntil);

        // if it's the same day but the time has passed, add 7 days
        if (nextTarget <= now)
        {
            nextTarget = nextTarget.AddDays(7);
        }

        return nextTarget - now;
    }

    /// <summary>
    /// Monthly schedule: runs on the 'dayOfMonth' at HH:mm.
    /// e.g. dayString="1", timeString="03:00" => next 1st of the month at 3 AM
    /// </summary>
    private static TimeSpan GetTimeUntilNextMonthly(string dayString, string timeString)
    {
        if (!TryParseHourMinute(timeString, out int hh, out int mm))
        {
            hh = 3; mm = 0;
        }
        if (!int.TryParse(dayString, out int dayOfMonth))
        {
            dayOfMonth = 1; // default to 1st
        }

        // clamp dayOfMonth to valid range 1-28 (simple approach)
        if (dayOfMonth < 1) dayOfMonth = 1;
        if (dayOfMonth > 28) dayOfMonth = 28; // avoid issues with 29-31

        DateTime now = DateTime.Now;
        // figure out if this month's day has passed
        int year = now.Year;
        int month = now.Month;
        DateTime targetThisMonth;
        try
        {
            targetThisMonth = new DateTime(year, month, dayOfMonth, hh, mm, 0);
        }
        catch
        {
            // if dayOfMonth invalid, fallback to the 1st
            targetThisMonth = new DateTime(year, month, 1, hh, mm, 0);
        }

        if (now > targetThisMonth)
        {
            // schedule for next month
            // e.g. if it's the 5th but dayOfMonth=1 => next month
            targetThisMonth = targetThisMonth.AddMonths(1);
        }
        return targetThisMonth - now;
    }

    /// <summary>
    /// Helper to parse "HH:mm" into hours & minutes.
    /// </summary>
    private static bool TryParseHourMinute(string timeString, out int hour, out int minute)
    {
        hour = 3;
        minute = 0;
        if (string.IsNullOrEmpty(timeString)) return false;

        var parts = timeString.Split(':');
        if (parts.Length != 2) return false;
        if (!int.TryParse(parts[0], out hour)) return false;
        if (!int.TryParse(parts[1], out minute)) return false;

        // clamp
        if (hour < 0 || hour > 23) return false;
        if (minute < 0 || minute > 59) return false;
        return true;
    }

    /// <summary>
    /// Loads scheduling environment variables (UPDATE_MODE, UPDATE_INTERVAL, UPDATE_TIME, UPDATE_DAY).
    /// </summary>
    private static void LoadSchedulingConfig()
    {
        // e.g. INTERVAL, DAILY, WEEKLY, MONTHLY
        updateMode = Environment.GetEnvironmentVariable("UPDATE_MODE")?.ToUpperInvariant() ?? "INTERVAL";

        // e.g. "10m", "1h" - only used if UPDATE_MODE=INTERVAL
        updateInterval = Environment.GetEnvironmentVariable("UPDATE_INTERVAL");

        // e.g. "03:00" for daily/weekly/monthly
        updateTime = Environment.GetEnvironmentVariable("UPDATE_TIME") ?? "03:00";

        // e.g. "SUNDAY" for weekly or "1" for monthly. (Optional)
        updateDay = Environment.GetEnvironmentVariable("UPDATE_DAY") ?? "1";

        Console.WriteLine($"Scheduling config: MODE={updateMode}, INTERVAL={updateInterval}, TIME={updateTime}, DAY={updateDay}");
    }

    /// <summary>
    /// Loads a comma-separated list of container images from the EXCLUDE_IMAGES environment variable.
    /// Stores them in 'excludeImages' set.
    /// </summary>
    private static void LoadExcludeImages()
    {
        string? envValue = Environment.GetEnvironmentVariable("EXCLUDE_IMAGES");
        if (!string.IsNullOrEmpty(envValue))
        {
            var images = envValue.Split(',')
                                 .Select(s => s.Trim())
                                 .Where(s => !string.IsNullOrWhiteSpace(s))
                                 .ToList();

            foreach (var img in images)
            {
                excludeImages.Add(img);
            }

            Console.WriteLine($"Loaded {excludeImages.Count} excluded images from EXCLUDE_IMAGES.");
            foreach (var e in excludeImages)
            {
                Console.WriteLine($" - {e}");
            }
        }
        else
        {
            Console.WriteLine("No EXCLUDE_IMAGES environment variable found. No images will be excluded.");
        }
    }

    /// <summary>
    /// Detects whether the OS is Windows or Linux/Mac, returning the corresponding Docker URI.
    /// </summary>
    private static string? DetectDockerEnvironment()
    {
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            Console.WriteLine("Windows environment detected.");
            return "npipe://./pipe/docker_engine";
        }
        else if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux) ||
                 RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
        {
            Console.WriteLine("Linux/Mac environment detected.");
            return System.IO.File.Exists("/var/run/docker.sock") ? "unix:///var/run/docker.sock" : null;
        }
        return null;
    }

    /// <summary>
    /// Checks all containers for a newer image. Only if a newer image is found,
    /// we create a backup, recreate the container, and roll back on failure.
    /// </summary>
    private static async Task CheckAndUpdateContainers()
    {
        if (dockerClient == null) return;

        var containers = await dockerClient.Containers.ListContainersAsync(
            new ContainersListParameters { All = true });

        foreach (var container in containers)
        {
            string containerId = container.ID;
            string containerName = container.Names.FirstOrDefault() ?? containerId;
            string fullImageName = container.Image;

            // 1) Skip containers that have been rolled back previously
            if (ignoredContainers.Contains(containerName))
            {
                Console.WriteLine($"\nContainer {containerName} is ignored (previous rollback). Skipping...");
                continue;
            }

            // 2) Skip if the container's image is in the exclude list
            //    Note: If the container is "openhab/openhab:latest" but you only put "openhab/openhab"
            //    in the exclude list, it won't match exactly. Consider a partial match if necessary.
            if (excludeImages.Contains(fullImageName))
            {
                Console.WriteLine($"\nContainer {containerName} uses excluded image {fullImageName}. Skipping...");
                continue;
            }

            Console.WriteLine($"\nChecking container {containerName} (ID: {containerId}) with image: {fullImageName}");

            // Parse base image name and tag
            var (baseImageName, imageTag) = SplitImageNameAndTag(fullImageName);
            string currentFullImageName = $"{baseImageName}:{imageTag}";

            // Pull the same tag to see if there's a newer version
            try
            {
                await dockerClient.Images.CreateImageAsync(
                    new ImagesCreateParameters
                    {
                        FromImage = baseImageName,
                        Tag = imageTag
                    },
                    null,
                    new Progress<JSONMessage>());
                Console.WriteLine($"Pulled {baseImageName}:{imageTag} (if a newer version was available).");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Error pulling {baseImageName}:{imageTag}: {ex.Message}");
                continue;
            }

            // Check if there's actually a new image ID
            string? updatedImageId = (await dockerClient.Images.ListImagesAsync(
                new ImagesListParameters()))
                .FirstOrDefault(img => (img.RepoTags != null) &&
                                       img.RepoTags.Contains(currentFullImageName))
                ?.ID;

            if (!string.IsNullOrEmpty(updatedImageId) && updatedImageId != container.ImageID)
            {
                // There's a newer image
                Console.WriteLine($"Detected update: old image ID {ShortId(container.ImageID)}, new image ID {ShortId(updatedImageId)}");

                // Create a backup tag for the old image
                string backupTag = $"backup-{DateTime.UtcNow:yyyyMMddHHmmss}";
                string backupFullName = $"{baseImageName}:{backupTag}";

                // 1) Tag the old image with the backup tag
                try
                {
                    await dockerClient.Images.TagImageAsync(
                        container.ImageID,
                        new ImageTagParameters
                        {
                            RepositoryName = baseImageName,
                            Tag = backupTag,
                            Force = true
                        });
                    Console.WriteLine($"Created backup: {backupFullName}");
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"Error creating backup for {fullImageName}: {ex.Message}");
                    // If backup fails, skip the update
                    continue;
                }

                // 2) Recreate the container with the new image
                bool recreateSuccess = false;
                try
                {
                    await RecreateContainerWithAllSettings(dockerClient, containerId, currentFullImageName);
                    recreateSuccess = true;
                    Console.WriteLine($"Container {containerName} recreated and started with {currentFullImageName}.");
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"Error recreating {containerName}: {ex.Message}");
                }

                // 3) Check container health if recreation succeeded
                if (recreateSuccess)
                {
                    bool containerHealthy = await CheckContainerHealth(containerName, containerCheckSeconds);
                    if (!containerHealthy)
                    {
                        Console.WriteLine($"Container {containerName} failed after update. Rolling back to {backupFullName}...");

                        try
                        {
                            // Remove the failed container
                            var newContainer = (await dockerClient.Containers.ListContainersAsync(
                                new ContainersListParameters { All = true }))
                                .FirstOrDefault(c => c.Names.Contains(containerName) || c.ID.StartsWith(containerId));
                            if (newContainer != null)
                            {
                                await dockerClient.Containers.RemoveContainerAsync(
                                    newContainer.ID,
                                    new ContainerRemoveParameters { Force = true });
                            }

                            // Recreate container with the backup image
                            await RecreateContainerWithAllSettings(dockerClient, containerId, backupFullName);
                            Console.WriteLine($"Container {containerName} has been rolled back to {backupFullName}.");

                            // Ignore this container for future updates
                            ignoredContainers.Add(containerName);
                            Console.WriteLine($"Container {containerName} will be ignored for future updates.");
                        }
                        catch (Exception ex)
                        {
                            Console.WriteLine($"Error rolling back {containerName}: {ex.Message}");
                        }
                    }
                    else
                    {
                        Console.WriteLine($"Container {containerName} is healthy after the update.");
                    }
                }
                else
                {
                    // If recreate failed immediately, roll back
                    Console.WriteLine($"Recreation of {containerName} failed. Rolling back to {backupFullName}...");
                    try
                    {
                        await RecreateContainerWithAllSettings(dockerClient, containerId, backupFullName);
                        Console.WriteLine($"Container {containerName} rolled back to {backupFullName}.");

                        ignoredContainers.Add(containerName);
                        Console.WriteLine($"Container {containerName} will be ignored for future updates.");
                    }
                    catch (Exception ex)
                    {
                        Console.WriteLine($"Failed to roll back {containerName}: {ex.Message}");
                    }
                }
            }
            else
            {
                Console.WriteLine("No update required. (No newer image found.)");
            }
        }
    }

    /// <summary>
    /// Splits a full image name (e.g., "registry.com/repo/app:1.2.3") into (baseImageName, tag).
    /// If no tag is present, returns "latest" as the tag.
    /// </summary>
    private static (string baseImageName, string imageTag) SplitImageNameAndTag(string imageName)
    {
        int lastColon = imageName.LastIndexOf(':');
        int lastSlash = imageName.LastIndexOf('/');

        if (lastColon > -1 && lastColon > lastSlash)
        {
            string baseName = imageName[..lastColon];
            string tag = imageName[(lastColon + 1)..];
            return (baseName, tag);
        }
        else
        {
            return (imageName, "latest");
        }
    }

    /// <summary>
    /// Stops, removes, then recreates the container with the same config, host config, networks, etc.,
    /// using the specified new image name.
    /// </summary>
    private static async Task RecreateContainerWithAllSettings(DockerClient client, string oldContainerId, string newFullImageName)
    {
        var details = await client.Containers.InspectContainerAsync(oldContainerId);

        // Stop/remove old container
        await client.Containers.StopContainerAsync(oldContainerId, new ContainerStopParameters());
        await client.Containers.RemoveContainerAsync(oldContainerId, new ContainerRemoveParameters { Force = true });

        // Build network config
        var endpointsConfig = new Dictionary<string, EndpointSettings>();
        if (details.NetworkSettings?.Networks != null)
        {
            endpointsConfig = details.NetworkSettings.Networks.ToDictionary(
                net => net.Key,
                net => new EndpointSettings
                {
                    Aliases = net.Value.Aliases,
                    DriverOpts = net.Value.DriverOpts,
                    EndpointID = net.Value.EndpointID,
                    Gateway = net.Value.Gateway,
                    GlobalIPv6Address = net.Value.GlobalIPv6Address,
                    GlobalIPv6PrefixLen = net.Value.GlobalIPv6PrefixLen,
                    IPAddress = net.Value.IPAddress,
                    IPAMConfig = net.Value.IPAMConfig,
                    IPPrefixLen = net.Value.IPPrefixLen,
                    IPv6Gateway = net.Value.IPv6Gateway,
                    Links = net.Value.Links,
                    MacAddress = net.Value.MacAddress,
                    NetworkID = net.Value.NetworkID
                }
            );
        }

        // Warning: if the container uses static IP addresses, Docker might fail to recreate if the IP is already in use

        // Prepare creation params
        var createParams = new CreateContainerParameters
        {
            // Original name
            Name = details.Name?.Trim('/') ?? "",

            Image = newFullImageName,

            // Copy original Config
            Tty = details.Config.Tty,
            AttachStdin = details.Config.AttachStdin,
            AttachStdout = details.Config.AttachStdout,
            AttachStderr = details.Config.AttachStderr,
            OpenStdin = details.Config.OpenStdin,
            StdinOnce = details.Config.StdinOnce,
            Env = details.Config.Env,
            Cmd = details.Config.Cmd,
            Entrypoint = details.Config.Entrypoint,
            Hostname = details.Config.Hostname,
            Domainname = details.Config.Domainname,
            User = details.Config.User,
            Labels = details.Config.Labels,
            WorkingDir = details.Config.WorkingDir,
            StopSignal = details.Config.StopSignal,
            ExposedPorts = details.Config.ExposedPorts,
            Shell = details.Config.Shell,

            HostConfig = details.HostConfig,

            NetworkingConfig = new NetworkingConfig
            {
                EndpointsConfig = endpointsConfig
            }
        };

        // Create and start new container
        var createResp = await client.Containers.CreateContainerAsync(createParams);
        await client.Containers.StartContainerAsync(createResp.ID, new ContainerStartParameters());
    }

    /// <summary>
    /// Checks if the container remains running (or exits with code 0) within 'waitSeconds'.
    /// Returns true if healthy, false if it exits with non-zero.
    /// </summary>
    private static async Task<bool> CheckContainerHealth(string containerName, int waitSeconds)
    {
        if (dockerClient == null) return true;

        int intervalMs = 2000; // check every 2 seconds
        int elapsed = 0;

        var containers = await dockerClient.Containers.ListContainersAsync(new ContainersListParameters { All = true });
        var match = containers.FirstOrDefault(c => c.Names.Contains(containerName));
        if (match == null)
        {
            Console.WriteLine($"CheckContainerHealth: cannot find container {containerName}.");
            return false;
        }
        string containerId = match.ID;

        while (elapsed < waitSeconds * 1000)
        {
            try
            {
                var info = await dockerClient.Containers.InspectContainerAsync(containerId);
                if (!info.State.Running)
                {
                    // If it's not running, check exit code
                    if (info.State.ExitCode == 0)
                    {
                        Console.WriteLine($"Container {containerName} exited normally (exit code 0).");
                        return true;
                    }
                    else
                    {
                        Console.WriteLine($"Container {containerName} exited with code {info.State.ExitCode}.");
                        return false;
                    }
                }
            }
            catch
            {
                // If we can't inspect, assume unhealthy
                return false;
            }

            await Task.Delay(intervalMs);
            elapsed += intervalMs;
        }

        // If we get here, container is still running after 'waitSeconds'
        return true;
    }

    /// <summary>
    /// Removes local backup images (with tags matching "repo:backup-YYYYMMddHHmmss") older than 30 days.
    /// </summary>
    private static async Task RemoveOldBackups()
    {
        if (dockerClient == null) return;

        Console.WriteLine("\nRemoving old backup images...");

        var images = await dockerClient.Images.ListImagesAsync(new ImagesListParameters());
        var now = DateTime.UtcNow;

        foreach (var image in images)
        {
            if (image.RepoTags == null) continue;

            foreach (var tag in image.RepoTags)
            {
                var match = BackupRegex().Match(tag);
                if (match.Success)
                {
                    string stamp = match.Groups["stamp"].Value; // "yyyyMMddHHmmss"
                    if (DateTime.TryParseExact(
                        stamp,
                        "yyyyMMddHHmmss",
                        CultureInfo.InvariantCulture,
                        DateTimeStyles.None,
                        out DateTime backupTime))
                    {
                        // Check if older than backupRetention
                        if (now - backupTime > backupRetention)
                        {
                            Console.WriteLine($"Removing old backup image: {tag}");
                            try
                            {
                                await dockerClient.Images.DeleteImageAsync(
                                    tag,
                                    new ImageDeleteParameters { Force = true });
                            }
                            catch (Exception ex)
                            {
                                Console.WriteLine($"Failed to remove backup image {tag}: {ex.Message}");
                            }
                        }
                    }
                }
            }
        }
    }

    private static string ShortId(string? id)
    {
        if (string.IsNullOrEmpty(id)) return string.Empty;
        return id.Length > 12 ? id[..12] : id;
    }

    [GeneratedRegex(@"^(?<repo>.+):backup-(?<stamp>\d{14})$")]
    private static partial Regex BackupRegex();
}