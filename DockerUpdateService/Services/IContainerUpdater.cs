namespace DockerUpdateService.Services;
internal interface IContainerUpdater { Task UpdateContainersAsync(CancellationToken ct = default); }
