// Services/PortainerService.cs
using System.Net.Http.Headers;
using Docker.DotNet;
using Docker.DotNet.Models;
using DockerUpdateService.Models;
using DockerUpdateService.Options;
using DockerUpdateService.Util;
using Microsoft.Extensions.Logging;

namespace DockerUpdateService.Services;

public sealed class PortainerService(
    HttpClient http, 
    IDockerClient docker, 
    DockerEngineService engine, 
    PortainerOptions opts, 
    ILogger<PortainerService> log)
{
    private readonly HttpClient _http = http;
    private readonly IDockerClient _docker = docker;
    private readonly DockerEngineService _engine = engine;
    private readonly PortainerOptions _opts = opts;
    private readonly ILogger<PortainerService> _log = log;

    public bool Enabled => _opts.Enabled;

    public async Task<(List<string> stackImages, HashSet<string> newlyIgnored)> CheckAndUpdatePortainerStacksAsync(CancellationToken ct)
    {
        var stackImages = new List<string>();
        var newlyIgnored = new HashSet<string>();

        if (!Enabled) return (stackImages, newlyIgnored);

        if (_http.BaseAddress is null && !string.IsNullOrWhiteSpace(_opts.Url))
            _http.BaseAddress = new Uri(_opts.Url!);

        _log.LogInformation("Querying Portainer stacks …");
        var listResp = await _http.GetAsync("/api/stacks", ct);
        if (!listResp.IsSuccessStatusCode)
        {
            _log.LogWarning("Portainer stack list failed: {StatusCode}", listResp.StatusCode);
            return (stackImages, newlyIgnored);
        }

        var stacks = await JsonSerializer.DeserializeAsync<List<PortainerStack>>(await listResp.Content.ReadAsStreamAsync(ct), cancellationToken: ct) ?? [];

        foreach (var stack in stacks)
        {
            _log.LogInformation("Checking stack '{Name}' (ID {Id}) …", stack.Name, stack.Id);

            var fileResp = await _http.GetAsync($"/api/stacks/{stack.Id}/file", ct);
            fileResp.EnsureSuccessStatusCode();

            var fileJson = await fileResp.Content.ReadAsStringAsync(ct);
            var yaml = JsonSerializer.Deserialize<StackFileResponse>(fileJson)?.StackFileContent ?? string.Empty;

            var images = YamlParse.ParseImages(yaml);

            var updateNeeded = false;
            foreach (var img in images)
            {
                var (repo, tag) = DockerEngineService.SplitImage(img);
                stackImages.Add($"{repo}");
                if (await _engine.ImageHasNewVersion(img))
                {
                    _log.LogInformation("  Update available for {Image}", img);
                    updateNeeded = true;
                }
            }
            if (!updateNeeded)
            {
                _log.LogInformation("  No updates for this stack.");
                continue;
            }

            var detailResp = await _http.GetAsync($"/api/stacks/{stack.Id}", ct);
            detailResp.EnsureSuccessStatusCode();
            StackEnv[] env = [];
            if (detailResp.IsSuccessStatusCode)
            {
                var envJson = await detailResp.Content.ReadAsStringAsync(ct);
                var proj = JsonSerializer.Deserialize<ComposeProject>(envJson);
                if (proj?.Env is not null)
                    env = [.. proj.Env.Select(e => new StackEnv(e.Name, e.Value))];
            }

            var detail = await JsonSerializer
                .DeserializeAsync<PortainerStackWithFile>(await detailResp.Content.ReadAsStreamAsync(ct), cancellationToken: ct);

            var envArr = env ?? detail?.Env ?? [];

            var payload = new
            {
                stackFileContent = yaml,
                env = envArr,
                prune = true
            };

            var body = new StringContent(JsonSerializer.Serialize(payload, JsonUtil.CamelCase()), Encoding.UTF8, "application/json");
            var url = $"/api/stacks/{stack.Id}?endpointId={stack.EndpointId}&method=string&pullImage=true&recreate=always";

            var upd = await _http.PutAsync(url, body, ct);
            if (!upd.IsSuccessStatusCode)
            {
                var err = await upd.Content.ReadAsStringAsync(ct);
                _log.LogWarning("  Stack update failed: {Status} – {Error}", upd.StatusCode, err);
                continue;
            }

            _log.LogInformation("  Stack redeployed successfully.");

            var related = await _docker.Containers.ListContainersAsync(new ContainersListParameters
            {
                All = true,
                Filters = new Dictionary<string, IDictionary<string, bool>>
                {
                    ["label"] = new Dictionary<string, bool>
                    {
                        [$"com.docker.compose.project={stack.Name}"] = true
                    }
                }
            }, ct);

            foreach (var ctr in related)
            {
                var cname = ctr.Names.FirstOrDefault()?.TrimStart('/');
                if (cname is not null && newlyIgnored.Add(cname))
                    _log.LogInformation("    Container {Name} will be ignored in future single-container checks.", cname);
            }
        }

        return (stackImages, newlyIgnored);
    }

    // Helper DTOs
    private sealed record PortainerStack(int Id, string Name, int EndpointId);
    private sealed record StackEnv(string Name, string? Value);
    private sealed record StackFileResponse(string StackFileContent);
    private sealed record PortainerStackWithFile(
        int Id,
        string Name,
        int EndpointId,
        string StackFileContent,
        StackEnv[]? Env);
}
