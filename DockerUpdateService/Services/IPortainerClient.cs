using DockerUpdateService.Models;
namespace DockerUpdateService.Services;
internal interface IPortainerClient
{
    Task<IReadOnlyList<PortainerStack>> GetStacksAsync(CancellationToken ct = default);
    Task<string> GetStackFileAsync(int id, CancellationToken ct = default);
    Task RedeployStackAsync(int id, string stackFile, IEnumerable<StackEnv> env, int endpointId, CancellationToken ct = default);
}
