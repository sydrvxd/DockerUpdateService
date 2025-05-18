using DockerUpdateService.Options;
using DockerUpdateService.Helpers;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace DockerUpdateService.Services;
internal sealed class UpdateWorker(
    ILogger<UpdateWorker> log,
    IPruner pruner,
    IStackUpdater stackUpdater,
    IContainerUpdater containerUpdater,
    IOptionsMonitor<SchedulingSettings> sched)
    : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await pruner.PruneAsync(stoppingToken);
                await stackUpdater.UpdateStacksAsync(stoppingToken);
                await containerUpdater.UpdateContainersAsync(stoppingToken);
            }
            catch (Exception ex)
            {
                log.LogError(ex, "Update cycle failed");
            }

            var delay = sched.CurrentValue.CalculateNextDelay();
            log.LogInformation("Next run in {Delay:g}", delay);
            await Task.Delay(delay, stoppingToken);
        }
    }
}
