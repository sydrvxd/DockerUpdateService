namespace DockerUpdateService.Services;
internal interface IStackUpdater { Task UpdateStacksAsync(CancellationToken ct = default); }
