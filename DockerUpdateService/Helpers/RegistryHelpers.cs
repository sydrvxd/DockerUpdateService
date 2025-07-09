using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
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

        /// <summary>
        /// Resolve a tag to its remote image digest for *any* OCI-compatible registry.
        /// </summary>
        public static async Task<string> GetRemoteDigestAsync(string image, HttpClient http, CancellationToken ct)
        {
            // ---------- Parse image reference ---------------------------------------
            // [registryhost[:port]/]repo[/sub]/image[:tag|@sha256]
            string registry = "registry-1.docker.io";
            string repository;
            string reference = "latest";

            var firstSlash = image.IndexOf('/');
            if (firstSlash > -1 && image[..firstSlash].Contains('.'))
            {
                registry = image[..firstSlash];
                image = image[(firstSlash + 1)..];
            }

            var at = image.IndexOf('@');
            var colon = image.LastIndexOf(':');       // tag separator
            if (at > -1) { reference = image[(at + 1)..]; repository = image[..at]; }
            else if (colon > -1) { reference = image[(colon + 1)..]; repository = image[..colon]; }
            else { repository = image; }

            if (registry == "registry-1.docker.io" && !repository.Contains('/'))
                repository = $"library/{repository}";

            // ---------- HEAD request for Docker-Content-Digest -----------------------
            var url = $"https://{registry}/v2/{repository}/manifests/{reference}";

            using var req = new HttpRequestMessage(HttpMethod.Head, url);
            req.Headers.Accept.Add(new("application/vnd.docker.distribution.manifest.v2+json"));

            using var res = await http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct);
            if (res.StatusCode == HttpStatusCode.Unauthorized && res.Headers.WwwAuthenticate.Count > 0)
            {
                // Follow Bearer challenge – only the most common flow (scope & realm) is implemented
                var ch = res.Headers.WwwAuthenticate.First();
                if (ch.Scheme.Equals("Bearer", StringComparison.OrdinalIgnoreCase))
                {
                    var realm = GetAuthParam(ch, "realm");
                    var svc = GetAuthParam(ch, "service");
                    var scope = GetAuthParam(ch, "scope");

                    var authUrl = $"{realm}?service={WebUtility.UrlEncode(svc)}&scope={WebUtility.UrlEncode(scope)}";
                    var token = await http.GetFromJsonAsync<TokenResponse>(authUrl, ct);

                    if (!string.IsNullOrWhiteSpace(token?.Token))
                    {
                        req.Headers.Authorization = new("Bearer", token.Token);
                        using var retry = await http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct);
                        retry.EnsureSuccessStatusCode();
                        return retry.Headers.GetValues("Docker-Content-Digest").FirstOrDefault() ?? string.Empty;
                    }
                }
            }

            if (res.IsSuccessStatusCode)
                return res.Headers.GetValues("Docker-Content-Digest").FirstOrDefault() ?? string.Empty;

            return string.Empty;    // caller logs the warning
        }

        private sealed record TokenResponse(string Token);

        /// <summary>
        /// Extract a key=value pair from the WWW-Authenticate parameter list:
        /// Bearer realm="https://auth.docker.io",service="registry.docker.io",scope="repository:lscr.io/linuxserver/prowlarr:pull"
        /// </summary>
        internal static string? GetAuthParam(this AuthenticationHeaderValue header, string key)
        {
            if (header?.Parameter is null)
                return null;

            foreach (var segment in header.Parameter.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
            {
                var kv = segment.Split('=', 2, StringSplitOptions.TrimEntries);
                if (kv.Length == 2 && kv[0].Trim('"').Equals(key, StringComparison.OrdinalIgnoreCase))
                    return kv[1].Trim('"');
            }
            return null;
        }

    }

}
