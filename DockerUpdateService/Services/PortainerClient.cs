using DockerUpdateService.Models;
using Microsoft.Extensions.Logging;
using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;

namespace DockerUpdateService.Services;

/// <summary>
/// Thin wrapper around Portainer HTTP API (v2) used only for the small subset we need:
/// * list stacks
/// * download the original compose file
/// * trigger a redeploy with optional env overrides
/// </summary>
internal sealed class PortainerClient(HttpClient http, ILogger<PortainerClient> log) : IPortainerClient
{
    private readonly HttpClient _http = http;
    private readonly JsonSerializerOptions _json = new() { PropertyNameCaseInsensitive = true };

    public async Task<IReadOnlyList<PortainerStack>> GetStacksAsync(CancellationToken ct = default) =>
        await _http.GetFromJsonAsync<IReadOnlyList<PortainerStack>>("/api/stacks", _json, ct)
            ?? [];

    public async Task<string> GetStackFileAsync(int id, CancellationToken ct = default)
    {
        using var res = await _http.GetAsync($"/api/stacks/{id}/file", ct);

        if (res.StatusCode == HttpStatusCode.NotFound)
        {
            throw new StackFileNotFoundException(id);
        }

        res.EnsureSuccessStatusCode();
        var content = await res.Content.ReadAsStringAsync(ct);
        return content;
    }

    public async Task RedeployStackAsync(
        int id,
        string yaml,
        IEnumerable<StackEnv> env,
        int endpointId,
        CancellationToken ct = default)
    {
        var body = new StringContent(
            JsonSerializer.Serialize(new
            {
                stackFileContent = yaml,
                env,
                prune = true
            }, _json), Encoding.UTF8, "application/json");

        var url = $"/api/stacks/{id}?endpointId={endpointId}&pullImage=true&recreate=always&method=string";
        using var res = await _http.PutAsync(url, body, ct);
        res.EnsureSuccessStatusCode();
    }
}

/// <summary>Thrown when Portainer returns 404 for /stacks/{id}/file.</summary>
internal sealed class StackFileNotFoundException(int id) : Exception($"Stack file not found for stack {id}")
{
    public int StackId { get; } = id;
}