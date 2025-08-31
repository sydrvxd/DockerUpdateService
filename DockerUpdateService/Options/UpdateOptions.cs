// Options/UpdateOptions.cs
using Cronos;

namespace DockerUpdateService.Options;

public sealed class UpdateOptions
{
    public string Mode { get; set; } = "INTERVAL"; // INTERVAL | DAILY | WEEKLY | MONTHLY | CRON
    public string? Interval { get; set; } = "10m"; // e.g. 10s, 5m, 3h
    public string TimeOfDay { get; set; } = "03:00"; // for DAILY/WEEKLY/MONTHLY
    public string Day { get; set; } = "Sunday"; // for WEEKLY or a number (1..28) for MONTHLY
    public string? Cron { get; set; } // for CRON mode

    public HashSet<string> ExcludeImages { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    public int ContainerCheckSeconds { get; set; } = 60;
    public int BackupRetentionDays { get; set; } = 14;

    public static UpdateOptions LoadFromEnvironment()
    {
        var o = new UpdateOptions();

        var schedule = Env("SCHEDULE"); // smart single string
        var mode = Env("SCHEDULE_MODE")?.ToUpperInvariant();
        var interval = Env("SCHEDULE_INTERVAL");
        var time = Env("SCHEDULE_TIME");
        var day = Env("SCHEDULE_DAY");
        var cron = Env("SCHEDULE_CRON");

        // Friendly SCHEDULE grammar
        if (!string.IsNullOrWhiteSpace(schedule))
        {
            var s = schedule!.Trim();
            if (s.StartsWith("cron:", StringComparison.OrdinalIgnoreCase))
            {
                mode = "CRON"; cron = s[5..].Trim();
            }
            else if (s.StartsWith("daily@", StringComparison.OrdinalIgnoreCase))
            {
                mode = "DAILY"; time = s[6..];
            }
            else if (s.StartsWith("weekly:", StringComparison.OrdinalIgnoreCase))
            {
                mode = "WEEKLY";
                var rest = s[7..];
                var parts = rest.Split('@', 2, StringSplitOptions.TrimEntries);
                day = parts[0];
                time = parts.Length > 1 ? parts[1] : "03:00";
            }
            else if (s.StartsWith("monthly:", StringComparison.OrdinalIgnoreCase))
            {
                mode = "MONTHLY";
                var rest = s[8..];
                var parts = rest.Split('@', 2, StringSplitOptions.TrimEntries);
                day = parts[0];
                time = parts.Length > 1 ? parts[1] : "03:00";
            }
            else
            {
                mode = "INTERVAL"; interval = s;
            }
        }

        o.Mode = mode ?? o.Mode;
        o.Interval = interval ?? o.Interval;
        o.TimeOfDay = time ?? o.TimeOfDay;
        o.Day = day ?? o.Day;
        o.Cron = cron;

        // Exclusion list (images or containers)
        var excl = Env("EXCLUDE") ?? Env("EXCLUDE_IMAGES") ?? "";
        var exclFile = Env("EXCLUDE_FILE");
        var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        void AddSplit(string? csv)
        {
            if (string.IsNullOrWhiteSpace(csv)) return;
            foreach (var p in csv.Split([',', ';', '\n'], StringSplitOptions.RemoveEmptyEntries))
            {
                var t = p.Trim();
                if (!string.IsNullOrWhiteSpace(t)) set.Add(t);
            }
        }
        AddSplit(excl);
        if (!string.IsNullOrWhiteSpace(exclFile) && File.Exists(exclFile))
            AddSplit(File.ReadAllText(exclFile));

        o.ExcludeImages = set;

        o.ContainerCheckSeconds = EnvInt("CONTAINER_CHECK_SECONDS") ?? o.ContainerCheckSeconds;
        o.BackupRetentionDays = EnvInt("BACKUP_RETENTION_DAYS") ?? o.BackupRetentionDays;

        return o;
    }

    private static string? Env(string name) => Environment.GetEnvironmentVariable(name);
    private static int? EnvInt(string name) => int.TryParse(Env(name), out var v) ? v : null;
}
