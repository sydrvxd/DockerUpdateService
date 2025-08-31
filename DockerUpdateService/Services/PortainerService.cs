// Services/PortainerService.cs
using Docker.DotNet;
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
    UpdateOptions updateOptions,
    ILogger<PortainerService> log)
{
    private readonly HttpClient _http = http;
    private readonly IDockerClient _docker = docker;
    private readonly DockerEngineService _engine = engine;
    private readonly PortainerOptions _opts = opts;
    private readonly UpdateOptions _update = updateOptions;
    private readonly ILogger<PortainerService> _log = log;

    private string? _jwt;

    public bool Enabled => _opts.Enabled;

    private async Task EnsureAuthAsync(CancellationToken ct)
    {
        if (_http.BaseAddress is null && !string.IsNullOrWhiteSpace(_opts.Url))
            _http.BaseAddress = new Uri(_opts.Url!);

        if (!string.IsNullOrWhiteSpace(_opts.ApiKey))
            return;

        if (string.IsNullOrWhiteSpace(_opts.Username) || string.IsNullOrWhiteSpace(_opts.Password))
            return;

        if (_jwt is not null) return;

        var payload = JsonSerializer.Serialize(new { username = _opts.Username, password = _opts.Password });
        var resp = await _http.PostAsync("/api/auth", new StringContent(payload, Encoding.UTF8, "application/json"), ct);
        resp.EnsureSuccessStatusCode();
        var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync(ct));
        if (doc.RootElement.TryGetProperty("jwt", out var jwtEl))
        {
            _jwt = jwtEl.GetString();
            if (!string.IsNullOrEmpty(_jwt))
                _http.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", _jwt);
        }
    }

    public async Task<(List<string> stackImages, HashSet<string> newlyIgnored)> CheckAndUpdatePortainerStacksAsync(CancellationToken ct)
    {
        var stackImages = new List<string>();
        var newlyIgnored = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        if (!Enabled) return (stackImages, newlyIgnored);

        await EnsureAuthAsync(ct);

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
            ct.ThrowIfCancellationRequested();

            // Only Docker stacks (1=swarm, 2=compose)
            if (stack.Type is not (1 or 2))
            {
                _log.LogInformation("Skipping non-Docker stack {Id} ({Type})", stack.Id, stack.Type);
                continue;
            }

            _log.LogInformation("Checking stack '{Name}' (ID {Id}) …", stack.Name, stack.Id);

            // Get YAML
            // 1) Get compose YAML for redeploy payload (unchanged)
            var fileResp = await _http.GetAsync($"/api/stacks/{stack.Id}/file", ct);
            fileResp.EnsureSuccessStatusCode();
            var fileJson = await fileResp.Content.ReadAsStringAsync(ct);
            var yaml = JsonSerializer.Deserialize<StackFileResponse>(fileJson)?.StackFileContent ?? string.Empty;

            // 2) Ask Docker which containers belong to this stack (most reliable)
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

            // Build a unique list of image refs actually used by the stack
            var imagesInStack = related
                .Select(c => c.Image) // repo:tag (not ID)
                .Where(s => !string.IsNullOrWhiteSpace(s))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();

            if (imagesInStack.Count == 0)
            {
                _log.LogInformation("  No containers found for stack {Name}; skipping.", stack.Name);
                continue;
            }

            _log.LogInformation("  Images in stack {Name}: {Images}", stack.Name, string.Join(", ", imagesInStack));

            bool updateNeeded = false;

            foreach (var img in imagesInStack)
            {
                // Skip digest-pinned images (immutable)
                if (img.Contains("@sha256:", StringComparison.OrdinalIgnoreCase))
                {
                    _log.LogInformation("  {Image} is digest-pinned; skipping update check.", img);
                    continue;
                }

                var (repo, tag) = DockerEngineService.SplitImage(img);
                var imageKey = repo; // used to ignore single-container updates later
                if (!stackImages.Contains(imageKey)) stackImages.Add(imageKey);

                // Apply exclusion list (both image & container names are substrings)
                if (_update.ExcludeImages.Any(x =>
                    repo.Contains(x, StringComparison.OrdinalIgnoreCase) ||
                    img.Contains(x, StringComparison.OrdinalIgnoreCase)))
                {
                    _log.LogInformation("  Excluded: {Image}", img);
                    continue;
                }

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

            // Fetch env for stack (UI mirrors this)
            var detailResp = await _http.GetAsync($"/api/stacks/{stack.Id}", ct);
            detailResp.EnsureSuccessStatusCode();

            var env = Array.Empty<StackEnv>();
            try
            {
                var envJson = await detailResp.Content.ReadAsStringAsync(ct);
                var proj = JsonSerializer.Deserialize<ComposeProject>(envJson);
                if (proj?.Env is not null)
                    env = proj.Env.Select(e => new StackEnv(e.Name, e.Value)).ToArray();
            }
            catch { }

            var payload = new
            {
                StackFileContent = yaml,
                Env = env,
                Prune = true
            };

            var body = new StringContent(JsonSerializer.Serialize(payload, JsonUtil.CamelCase()), Encoding.UTF8, "application/json");
            var url = $"/api/stacks/{stack.Id}?endpointId={stack.EndpointId}&method=string&pullImage=1&recreate=always";

            _log.LogInformation("  Redeploying stack {Name} via Portainer API …", stack.Name);
            var upd = await _http.PutAsync(url, body, ct);
            if (!upd.IsSuccessStatusCode)
            {
                var err = await upd.Content.ReadAsStringAsync(ct);
                _log.LogWarning("  Stack update failed: {Status} – {Error}", upd.StatusCode, err);
                continue;
            }

            _log.LogInformation("  Stack redeployed successfully.");

            // Ignore compose project containers in the single-container pass
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
                    _log.LogInformation("    Container {Name} will be ignored in single-container checks.", cname);
            }
        }

        return (stackImages, newlyIgnored);
    }

    // DTOs
    private sealed record PortainerStack(int Id, string Name, int EndpointId, int Type);
    private sealed record StackEnv(string Name, string? Value);
    private sealed record StackFileResponse(string StackFileContent);
}
