// Options/UpdateOptions.cs
namespace DockerUpdateService.Options;

public sealed class UpdateOptions
{
    public string Mode { get; init; } = "INTERVAL";  // INTERVAL | DAILY | WEEKLY | MONTHLY
    public string? Interval { get; init; } = "10m";  // e.g. 10m, 30s, 1h
    public string TimeOfDay { get; init; } = "03:00"; // HH:mm
    public string Day { get; init; } = "1";           // 'Monday' for weekly or '1..28' monthly

    public HashSet<string> ExcludeImages { get; init; } = new(StringComparer.OrdinalIgnoreCase);

    public int BackupRetentionDays { get; set; } = 5;
    public int ContainerCheckSeconds { get; set; } = 10;

    public static UpdateOptions LoadFromEnvironment()
    {
        var o = new UpdateOptions
        {
            Mode = (Environment.GetEnvironmentVariable("UPDATE_MODE") ?? "INTERVAL").Trim().ToUpperInvariant(),
            Interval = Environment.GetEnvironmentVariable("UPDATE_INTERVAL") ?? "10m",
            TimeOfDay = Environment.GetEnvironmentVariable("UPDATE_TIME") ?? "03:00",
            Day = Environment.GetEnvironmentVariable("UPDATE_DAY") ?? "1"
        };

        var exclude = Environment.GetEnvironmentVariable("EXCLUDE_IMAGES");
        if (!string.IsNullOrWhiteSpace(exclude))
        {
            foreach (var s in exclude.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
                o.ExcludeImages.Add(s);
        }

        if (int.TryParse(Environment.GetEnvironmentVariable("BACKUP_RETENTION_DAYS"), out var d) && d > 0)
            o.BackupRetentionDays = d;

        if (int.TryParse(Environment.GetEnvironmentVariable("CONTAINER_CHECK_SECONDS"), out var cs) && cs > 0)
            o.ContainerCheckSeconds = cs;

        return o;
    }
}
