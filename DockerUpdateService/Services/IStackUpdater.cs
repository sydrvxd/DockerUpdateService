namespace DockerUpdateService.Services;
internal interface IStackUpdater 
{
    HashSet<string> IgnoredContainers { get; }
    HashSet<string> StackImages { get; }
    Task UpdateStacksAsync(CancellationToken ct = default); 
}
