namespace DockerUpdateService.Options;

public enum UpdateMode { Interval, Daily, Weekly, Monthly }

public sealed record SchedulingSettings
{
    public const string Section = "Schedule";

    public UpdateMode Mode { get; init; } = UpdateMode.Interval;

    public TimeSpan Interval { get; init; } = TimeSpan.FromMinutes(10);

    public TimeOnly TimeOfDay { get; init; } = new(03, 00);

    public DayOfWeek DayOfWeek { get; init; } = DayOfWeek.Monday;
}
