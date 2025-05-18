using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using DockerUpdateService.Models;

namespace DockerUpdateService.Services;
internal sealed class PortainerClient(HttpClient http) : IPortainerClient
{
    private readonly JsonSerializerOptions _json = new() {PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

    public async Task<IReadOnlyList<PortainerStack>> GetStacksAsync(CancellationToken ct = default) =>
        await http.GetFromJsonAsync<IReadOnlyList<PortainerStack>>("/api/stacks", _json, ct) ?? [];

    public async Task<string> GetStackFileAsync(int id, CancellationToken ct = default)
    {
        using var res = await http.GetAsync($"/api/stacks/{id}/file", ct);
        res.EnsureSuccessStatusCode();
        var wrapper = await res.Content.ReadFromJsonAsync<StackFileResponse>(_json, ct);
        return wrapper?.StackFileContent ?? string.Empty;
    }

    public async Task RedeployStackAsync(int id, string yaml, IEnumerable<StackEnv> env, int endpointId, CancellationToken ct = default)
    {
        var body = new StringContent(JsonSerializer.Serialize(new
        {
            stackFileContent = yaml,
            env,
            prune = true
        }, _json), Encoding.UTF8, "application/json");

        var url = $"/api/stacks/{id}?endpointId={endpointId}&pullImage=true&recreate=always&method=string";
        using var res = await http.PutAsync(url, body, ct);
        res.EnsureSuccessStatusCode();
    }
}
