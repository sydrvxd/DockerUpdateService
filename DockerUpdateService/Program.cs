
using Docker.DotNet;
using Docker.DotNet.Models;
using System.Runtime.InteropServices;


await Main();


partial class Program
{
    private static DockerClient? dockerClient;

    static async Task Main()
    {
        Console.WriteLine("Docker Auto-Updater has started...");

        // Detect Docker environment (Windows or Linux/Mac)
        string? dockerUri = DetectDockerEnvironment();
        if (dockerUri == null)
        {
            Console.WriteLine("No Docker environment detected. Exiting program.");
            return;
        }

        dockerClient = new DockerClientConfiguration(new Uri(dockerUri)).CreateClient();
        Console.WriteLine($"Connected to Docker API at: {dockerUri}");


        //while (true)
        //{
        //    try
        //    {
        //        await CheckAndUpdateContainers();
        //    }
        //    catch (Exception ex)
        //    {
        //        Console.WriteLine($"Fehler: {ex.Message}");
        //    }

        //    await Task.Delay(TimeSpan.FromMinutes(10)); // Alle 10 Minuten prüfen
        //}

        while (true)
        {
            try
            {
                await CheckAndUpdateContainers();
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Error during update: {ex.Message}");
            }

            // Wait, e.g., 10 minutes before the next check
            await Task.Delay(TimeSpan.FromSeconds(20));
        }
    }

    /// <summary>
    /// Detects whether the OS is Windows or Linux/Mac and returns the corresponding Docker URI.
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
            return File.Exists("/var/run/docker.sock") ? "unix:///var/run/docker.sock" : null;
        }
        return null;
    }

    /// <summary>
    /// Checks all containers to see if their underlying image has been updated.
    /// If so, recreates the container with all of the same settings.
    /// </summary>
    private static async Task CheckAndUpdateContainers()
    {
        if (dockerClient != null)
        {
            var containers = await dockerClient.Containers.ListContainersAsync(
                new ContainersListParameters { All = true });

            foreach (var container in containers)
            {
                string containerId = container.ID;
                string fullImageName = container.Image;

                Console.WriteLine($"\nChecking container {container.Names[0]} (ID: {containerId}) with image: {fullImageName}");

                // Extract base image and tag from the container's image (e.g., "myrepo/image:1.2.3")
                var (baseImageName, imageTag) = SplitImageNameAndTag(fullImageName);

                // Pull the same image:tag that the container is currently using
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
                    Console.WriteLine($"Pulled image {baseImageName}:{imageTag} (if an update was available).");
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"Error pulling {baseImageName}:{imageTag}: {ex.Message}");
                    continue;
                }

                // Check if the newly pulled image has a different ID than the container's current image ID
                string updatedFullImageName = $"{baseImageName}:{imageTag}";
                string? updatedImageId = (await dockerClient.Images.ListImagesAsync(
                    new ImagesListParameters()))
                    .FirstOrDefault(img => (img.RepoTags != null) &&
                                           img.RepoTags.Contains(updatedFullImageName))
                    ?.ID;

                // Only recreate if there's a new image
                if (!string.IsNullOrEmpty(updatedImageId) &&
                    updatedImageId != container.ImageID)
                {
                    Console.WriteLine($"Detected update: old image ID {ShortId(container.ImageID)}, "
                                      + $"new image ID {ShortId(updatedImageId)}");

                    // Recreate the container with the same settings
                    try
                    {
                        await RecreateContainerWithAllSettings(dockerClient, containerId, updatedFullImageName);
                        Console.WriteLine($"Container {container.Names[0]} has been recreated and started.");
                    }
                    catch (Exception ex)
                    {
                        Console.WriteLine($"Error recreating {container.Names[0]}: {ex.Message}");
                    }
                }
                else
                {
                    Console.WriteLine("No update required.");
                }
            }
        }
    }

    /// <summary>
    /// Splits a full image name (e.g., "myregistry.com/repo/app:1.2.3")
    /// into (baseImageName, tag). If no tag is found, returns "latest".
    /// </summary>
    private static (string baseImageName, string imageTag) SplitImageNameAndTag(string imageName)
    {
        // Example imageName: "myregistry.com/repo/app:1.2.3"
        // We look for the last colon that occurs after the last slash.
        int lastColon = imageName.LastIndexOf(':');
        int lastSlash = imageName.LastIndexOf('/');

        if (lastColon > -1 && lastColon > lastSlash)
        {
            // There's a tag
            string baseName = imageName.Substring(0, lastColon);
            string tag = imageName.Substring(lastColon + 1);
            return (baseName, tag);
        }
        else
        {
            // No explicit tag, assume "latest"
            return (imageName, "latest");
        }
    }

    /// <summary>
    /// Stops and removes the container, then recreates it with the same config/hostconfig/networks, etc.
    /// </summary>
    private static async Task RecreateContainerWithAllSettings(DockerClient client, string containerId, string newImageName)
    {
        // Inspect container before removing it to capture all current settings
        var details = await client.Containers.InspectContainerAsync(containerId);

        // Stop the container
        await client.Containers.StopContainerAsync(containerId, new ContainerStopParameters());

        // Remove the container
        await client.Containers.RemoveContainerAsync(containerId, new ContainerRemoveParameters { Force = true });

        // --- Build up the network configurations ---
        // If container is connected to multiple networks, create an EndpointSettings for each
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

        // *** Warning ***
        // If static IP addresses are set here, Docker might fail to recreate the container if the IP is already taken.
        // You may need to remove or modify these fields if you want Docker to assign IP addresses automatically.

        // Assemble the create parameters
        var createParams = new CreateContainerParameters
        {
            // Original name (Docker stores it with a leading slash)
            Name = details.Name?.Trim('/') ?? "",

            // Everything from the original Config
            Image = newImageName,
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

            // HostConfig includes volumes, port bindings, resource limits, etc.
            HostConfig = details.HostConfig,

            // Networking settings
            NetworkingConfig = new NetworkingConfig
            {
                EndpointsConfig = endpointsConfig
            }
        };

        // Create the new container
        var createResp = await client.Containers.CreateContainerAsync(createParams);

        // Start the new container
        await client.Containers.StartContainerAsync(createResp.ID, new ContainerStartParameters());
    }

    /// <summary>
    /// Helper to shorten long Docker IDs in logs.
    /// </summary>
    private static string ShortId(string id)
    {
        if (string.IsNullOrEmpty(id)) return "";
        return id.Length > 12 ? id[..12] : id;
    }
}