// Services/DockerUpdateWorker.cs
using DockerUpdateService.Options;
using DockerUpdateService.Util;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace DockerUpdateService.Services;

public sealed class DockerUpdateWorker : BackgroundService
{
    private readonly UpdateOptions _update;
    private readonly PortainerOptions _portainer;
    private readonly DockerEngineService _docker;
    private readonly PortainerService _port;
    private readonly ILogger<DockerUpdateWorker> _log;

    private readonly HashSet<string> _ignoredContainers = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<string> _stackImages = new();
    private readonly HashSet<string> _excludeImages;

    public DockerUpdateWorker(UpdateOptions update, PortainerOptions portainer, DockerEngineService docker, PortainerService port, ILogger<DockerUpdateWorker> log)
    {
        _update = update;
        _portainer = portainer;
        _docker = docker;
        _port = port;
        _log = log;
        _excludeImages = update.ExcludeImages;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _log.LogInformation("DockerUpdateService started. Scheduling => MODE={Mode} INTERVAL={Interval} TIME={Time} DAY={Day}", _update.Mode, _update.Interval, _update.TimeOfDay, _update.Day);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await _docker.RemoveOldBackupsAndUnusedImages(stoppingToken);

                if (_port.Enabled)
                {
                    _stackImages.Clear();
                    var (imgs, newlyIgnored) = await _port.CheckAndUpdatePortainerStacksAsync(stoppingToken);
                    _stackImages.AddRange(imgs);
                    foreach (var c in newlyIgnored) _ignoredContainers.Add(c);
                }

                await _docker.CheckAndUpdateContainers(_excludeImages, _ignoredContainers, _stackImages, stoppingToken);
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
