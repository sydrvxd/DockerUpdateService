using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using static System.Net.WebRequestMethods;

namespace DockerUpdateService.Helpers
{
    public static class RegistryHelpers
    {
        public static (string registry, string repo, string tag) SplitReference(string r)
        {
            // default registry
            string registry = "https://registry-1.docker.io";
            string tag = "latest";
            string? repo;
            if (r.Contains('/')) 
            { 
                repo = r; 
            } 
            else 
            { 
                repo = "library/" + r;
            }
            if (r.Contains(':')) 
            { 
                tag = r[(r.LastIndexOf(':') + 1)..]; 
                repo = r[..r.LastIndexOf(':')]; 
            }
            return (registry, repo, tag);
        }

        public static async Task<string> GetRemoteDigestAsync(string reg, string repo, string tag, HttpClient http, CancellationToken ct)
        {
            // 1) get Bearer token
            string tokUrl = $"https://auth.docker.io/token?service=registry.docker.io&scope=repository:{repo}:pull";
            string token = JsonDocument.Parse(await http.GetStringAsync(tokUrl, ct))
                              .RootElement.GetProperty("token").GetString()!;

            // 2) HEAD manifest
            var req = new HttpRequestMessage(HttpMethod.Head, $"{reg}/v2/{repo}/manifests/{tag}");
            req.Headers.Accept.ParseAdd("application/vnd.docker.distribution.manifest.v2+json");
            req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);

            var resp = await http.SendAsync(req, ct);
            return resp.Headers.TryGetValues("Docker-Content-Digest", out var v) ? v.First() : "";
        }
    }

}
