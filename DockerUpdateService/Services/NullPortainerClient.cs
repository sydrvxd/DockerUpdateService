using DockerUpdateService.Models;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace DockerUpdateService.Services;

/// <summary>
/// No‑op implementation used when Portainer integration is disabled.
/// </summary>
internal sealed class NullPortainerClient : IPortainerClient
{
    public Task<IReadOnlyList<PortainerStack>> GetStacksAsync(CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyList<PortainerStack>>([]);

    public Task<string> GetStackFileAsync(int id, CancellationToken ct = default) =>
        Task.FromResult(string.Empty);

    public Task RedeployStackAsync(int id, string stackFile, IEnumerable<StackEnv> env, int endpointId, CancellationToken ct = default) =>
        Task.CompletedTask;
}
