// Options/PortainerOptions.cs
namespace DockerUpdateService.Options;

public sealed class PortainerOptions
{
    public string? Url { get; init; }
    public string? ApiKey { get; init; }
    public bool InsecureTls { get; init; } = true;

    public bool Enabled => !string.IsNullOrWhiteSpace(Url) && !string.IsNullOrWhiteSpace(ApiKey);

    public static PortainerOptions LoadFromEnvironment()
    {
        return new PortainerOptions
        {
            Url = Environment.GetEnvironmentVariable("PORTAINER_URL"),
            ApiKey = Environment.GetEnvironmentVariable("PORTAINER_API_KEY"),
            InsecureTls = (Environment.GetEnvironmentVariable("PORTAINER_INSECURE") ?? "true")
                .Equals("true", StringComparison.OrdinalIgnoreCase)
        };
    }
}
