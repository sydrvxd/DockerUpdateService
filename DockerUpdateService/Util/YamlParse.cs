// Util/YamlParse.cs
namespace DockerUpdateService.Util;

internal static class YamlParse
{
    public static List<string> ParseImages(string yaml)
    {
        var list = new List<string>();
        foreach (var line in yaml.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            var t = line.Trim();
            if (t.StartsWith("image:", StringComparison.OrdinalIgnoreCase))
                list.Add(t["image:".Length..].Trim());
        }
        return list;
    }
}
