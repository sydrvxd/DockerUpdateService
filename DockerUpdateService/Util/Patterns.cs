namespace DockerUpdateService.Util;

internal static partial class Patterns
{
    [GeneratedRegex(@"^(?<repo>.+):backup-(?<stamp>\d{14})$")]
    internal static partial Regex BackupRegex();

    [GeneratedRegex(@"^(?<repo>[^:@\s]+(?:\/[^:@\s]+)*):(?:(\$\{[^}:]+:-)?(?<tag>[^@]+?))(?:@.*)?$")]
    internal static partial Regex ImageRegex();
}