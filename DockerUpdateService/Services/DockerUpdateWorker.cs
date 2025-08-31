using DockerUpdateService.Options;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace DockerUpdateService.Services;

public sealed class DockerUpdateWorker(
    UpdateOptions update,
    PortainerOptions portainer,
    DockerEngineService docker,
    PortainerService port,
    ILogger<DockerUpdateWorker> log) : BackgroundService
{
    private readonly UpdateOptions _update = update;
    private readonly PortainerOptions _portainer = portainer; 
    private readonly DockerEngineService _docker = docker;
    private readonly PortainerService _port = port;
    private readonly ILogger<DockerUpdateWorker> _log = log;

    private readonly HashSet<string> _ignoredContainers = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<string> _stackImages = [];
    private readonly HashSet<string> _excludeImages = update.ExcludeImages;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _log.LogInformation("DockerUpdateService started. Scheduling => MODE={Mode} INTERVAL={Interval} TIME={Time} DAY={Day} CRON={Cron}",
            _update.Mode, _update.Interval, _update.TimeOfDay, _update.Day, _update.Cron);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                // 1) Portainer stacks
                if (_port.Enabled)
                {
                    _stackImages.Clear();
                    var (imgs, newlyIgnored) = await _port.CheckAndUpdatePortainerStacksAsync(stoppingToken);
                    _stackImages.AddRange(imgs);
                    foreach (var c in newlyIgnored) _ignoredContainers.Add(c);
                }

                // 2) Non-stack containers
                await _docker.CheckAndUpdateContainers(_excludeImages, _ignoredContainers, _stackImages, stoppingToken);

                // 3) Prune
                await _docker.PruneUnusedImages(_excludeImages, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _log.LogError(ex, "Error during update cycle.");
            }

            var wait = Scheduler.NextDelay(_update);
            _log.LogInformation("Next check in {Wait}", wait);
            try
            {
                await Task.Delay(wait, stoppingToken);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }
    }
}
