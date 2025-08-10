// Services/Scheduler.cs
using DockerUpdateService.Options;

namespace DockerUpdateService.Services;

internal static class Scheduler
{
    public static TimeSpan NextDelay(UpdateOptions o, DateTime? nowOverride = null)
    {
        var now = nowOverride ?? DateTime.Now;
        return o.Mode switch
        {
            "DAILY"   => UntilNextDaily(o.TimeOfDay, now),
            "WEEKLY"  => UntilNextWeekly(o.Day, o.TimeOfDay, now),
            "MONTHLY" => UntilNextMonthly(o.Day, o.TimeOfDay, now),
            _         => ParseInterval(o.Interval ?? "10m")
        };
    }

    private static TimeSpan ParseInterval(string s)
    {
        if (s.Length < 2) return TimeSpan.FromMinutes(10);
        var suffix = s[^1];
        if (!int.TryParse(s[..^1], out int n)) return TimeSpan.FromMinutes(10);
        return suffix switch
        {
            's' or 'S' => TimeSpan.FromSeconds(n),
            'm' or 'M' => TimeSpan.FromMinutes(n),
            'h' or 'H' => TimeSpan.FromHours(n),
            'd' or 'D' => TimeSpan.FromDays(n),
            _ => TimeSpan.FromMinutes(10)
        };
    }

    private static bool TryParseHHmm(string t, out int h, out int m)
    {
        h = 3; m = 0;
        var parts = t.Split(':', 2);
        return parts.Length == 2 &&
               int.TryParse(parts[0], out h) && h is >= 0 and <= 23 &&
               int.TryParse(parts[1], out m) && m is >= 0 and <= 59;
    }

    private static TimeSpan UntilNextDaily(string time, DateTime now)
    {
        if (!TryParseHHmm(time, out int h, out int m)) { h = 3; m = 0; }
        var target = new DateTime(now.Year, now.Month, now.Day, h, m, 0);
        if (now >= target) target = target.AddDays(1);
        return target - now;
    }

    private static TimeSpan UntilNextWeekly(string day, string time, DateTime now)
    {
        if (!TryParseHHmm(time, out int h, out int m)) { h = 3; m = 0; }
        if (!Enum.TryParse(day, true, out DayOfWeek targetDay)) targetDay = DayOfWeek.Sunday;
        int daysDiff = ((int)targetDay - (int)now.DayOfWeek + 7) % 7;
        var target = new DateTime(now.Year, now.Month, now.Day, h, m, 0).AddDays(daysDiff);
        if (now >= target) target = target.AddDays(7);
        return target - now;
    }

    private static TimeSpan UntilNextMonthly(string day, string time, DateTime now)
    {
        if (!TryParseHHmm(time, out int h, out int m)) { h = 3; m = 0; }
        _ = int.TryParse(day, out int d);
        d = Math.Clamp(d, 1, 28);
        var target = new DateTime(now.Year, now.Month, d, h, m, 0);
        if (now >= target) target = target.AddMonths(1);
        return target - now;
    }
}
