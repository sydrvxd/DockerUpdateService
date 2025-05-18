using DockerUpdateService.Options;

namespace DockerUpdateService.Helpers;

internal static class SchedulingExtensions
{
    public static TimeSpan CalculateNextDelay(this SchedulingSettings s) =>
        s.Mode switch
        {
            UpdateMode.Daily   => UntilNext(s.TimeOfDay),
            UpdateMode.Weekly  => UntilNext(s.DayOfWeek, s.TimeOfDay),
            UpdateMode.Monthly => UntilNextMonthly(s.TimeOfDay),
            _                  => s.Interval
        };

    private static TimeSpan UntilNext(TimeOnly at)
    {
        var now = DateTime.Now;
        var target = now.Date + at.ToTimeSpan();
        if (target <= now) target = target.AddDays(1);
        return target - now;
    }

    private static TimeSpan UntilNext(DayOfWeek dow, TimeOnly at)
    {
        var now = DateTime.Now;
        int diff = ((int)dow - (int)now.DayOfWeek + 7) % 7;
        var target = (now.Date + at.ToTimeSpan()).AddDays(diff);
        if (target <= now) target = target.AddDays(7);
        return target - now;
    }

    private static TimeSpan UntilNextMonthly(TimeOnly at)
    {
        var now = DateTime.Now;
        var nextMonth = new DateTime(now.Year, now.Month, 1).AddMonths(1);
        var target = nextMonth + at.ToTimeSpan();
        return target - now;
    }
}
