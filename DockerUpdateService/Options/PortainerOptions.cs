namespace DockerUpdateService.Options;

public sealed class PortainerOptions
{
    public bool Enabled { get; init; }
    public string? Url { get; init; }
    public string? ApiKey { get; init; } // X-API-Key
    public string? Username { get; init; }
    public string? Password { get; init; }
    public bool InsecureTls { get; init; }

    public static PortainerOptions LoadFromEnvironment()
    {
        return new PortainerOptions
        {
            Enabled = EnvBool("PORTAINER_ENABLED") ?? !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("PORTAINER_URL")),
            Url = Environment.GetEnvironmentVariable("PORTAINER_URL")?.TrimEnd('/'),
            ApiKey = Environment.GetEnvironmentVariable("PORTAINER_API_KEY"),
            Username = Environment.GetEnvironmentVariable("PORTAINER_USERNAME"),
            Password = Environment.GetEnvironmentVariable("PORTAINER_PASSWORD"),
            InsecureTls = EnvBool("PORTAINER_INSECURE_TLS") ?? false
        };
    }

    private static bool? EnvBool(string name)
        => bool.TryParse(Environment.GetEnvironmentVariable(name), out var v) ? v : null;
}