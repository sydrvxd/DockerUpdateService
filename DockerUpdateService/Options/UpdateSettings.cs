using System.ComponentModel.DataAnnotations;

namespace DockerUpdateService.Options;

public sealed record UpdateSettings
{
    public const string Section = "Update";

    [Required]
    public required PortainerOptions Portainer { get; init; }

    public string[] ExcludeImages { get; init; } = [];

    public TimeSpan BackupRetention { get; init; } = TimeSpan.FromDays(5);

    public sealed record PortainerOptions
    {
        public string? Url { get; init; }
        public string? ApiKey { get; init; }
    }
}
