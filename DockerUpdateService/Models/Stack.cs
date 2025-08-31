namespace DockerUpdateService.Models;

/// <summary>Root object returned by the /api/stacks/{id} endpoint.</summary>
public sealed class ComposeProject
{
    [JsonPropertyName("Id")] public int Id { get; init; }
    [JsonPropertyName("Name")] public required string Name { get; init; }
    [JsonPropertyName("Type")] public int Type { get; init; } // 1=Swarm, 2=Compose
    [JsonPropertyName("EndpointId")] public int EndpointId { get; init; }
    [JsonPropertyName("SwarmId")] public string? SwarmId { get; init; }
    [JsonPropertyName("EntryPoint")] public required string EntryPoint { get; init; }
    [JsonPropertyName("Env")] public IReadOnlyList<EnvironmentVariable>? Env { get; init; }
    [JsonPropertyName("ProjectPath")] public required string ProjectPath { get; init; }
    [JsonPropertyName("CreationDate")] public long CreationDate { get; init; }
    [JsonPropertyName("CreatedBy")] public required string CreatedBy { get; init; }
    [JsonPropertyName("UpdateDate")] public long UpdateDate { get; init; }
    [JsonPropertyName("UpdatedBy")] public required string UpdatedBy { get; init; }
}

public sealed class EnvironmentVariable
{
    [JsonPropertyName("name")] public required string Name { get; init; }
    [JsonPropertyName("value")] public string? Value { get; init; }
}
