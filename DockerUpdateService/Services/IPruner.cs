namespace DockerUpdateService.Services;
internal interface IPruner { Task PruneAsync(CancellationToken ct = default); }
