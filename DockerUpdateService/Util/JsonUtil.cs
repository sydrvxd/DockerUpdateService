// Util/JsonUtil.cs
namespace DockerUpdateService.Util;

internal static class JsonUtil
{
    internal static JsonSerializerOptions CamelCase() => new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };
}
