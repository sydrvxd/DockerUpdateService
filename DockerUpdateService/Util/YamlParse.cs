namespace DockerUpdateService.Util;

internal static class YamlParse
{
    /// <summary>
    /// Naive image scanner for compose YAML. It just collects all "image:" lines.
    /// Robust enough for our update decision.
    /// </summary>
    public static List<string> ParseImages(string yaml)
    {
        var list = new List<string>();
        foreach (var line in yaml.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            var t = line.Trim();
            if (t.StartsWith("image:", StringComparison.OrdinalIgnoreCase))
                list.Add(t["image:".Length..].Trim().Trim('"', '\''));
        }
        return list;
    }
}
