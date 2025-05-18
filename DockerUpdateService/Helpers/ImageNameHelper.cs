using System.Text.RegularExpressions;

namespace DockerUpdateService.Helpers;

internal static partial class ImageNameHelper
{
    public static (string Repo, string Tag) Split(string reference)
    {
        var match = ImageRegex().Match(reference);
        if (match.Success)
            return (match.Groups["repo"].Value, match.Groups["tag"].Value.Replace("}", string.Empty));

        int at = reference.IndexOf('@');
        if (at >= 0) reference = reference[..at];
        int lastColon = reference.LastIndexOf(':');
        int lastSlash = reference.LastIndexOf('/');
        return (lastColon > lastSlash && lastColon >= 0)
            ? (reference[..lastColon], reference[(lastColon + 1)..])
            : (reference, "latest");
    }

    [GeneratedRegex(@"^(?<repo>[^:@\s]+(?:\/[^:@\s]+)*):(?:(\$\{[^}:]+:-)?(?<tag>[^@]+?))(?:@.*)?$")]
    private static partial Regex ImageRegex();
}
