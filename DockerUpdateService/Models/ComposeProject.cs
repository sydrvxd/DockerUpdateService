using System.Text.Json.Serialization;

namespace DockerUpdateService.Models;

/// <summary>
///     Root object returned by the compose-stack API.
/// </summary>
public sealed class ComposeProject
{
    [JsonPropertyName("Id")]
    public int Id { get; init; }

    [JsonPropertyName("Name")]
    public required string Name { get; init; }

    [JsonPropertyName("Type")]
    public int Type { get; init; }

    [JsonPropertyName("EndpointId")]
    public int EndpointId { get; init; }

    [JsonPropertyName("SwarmId")]
    public string? SwarmId { get; init; }

    [JsonPropertyName("EntryPoint")]
    public required string EntryPoint { get; init; }

    /// <summary>Environment variables passed to the stack.</summary>
    [JsonPropertyName("Env")]
    public IReadOnlyList<EnvironmentVariable>? Env { get; init; }

    [JsonPropertyName("ResourceControl")]
    public required ResourceControl ResourceControl { get; init; }

    [JsonPropertyName("Status")]
    public int Status { get; init; }

    [JsonPropertyName("ProjectPath")]
    public required string ProjectPath { get; init; }

    /// <summary>UNIX epoch (seconds) when the stack was created.</summary>
    [JsonPropertyName("CreationDate")]
    public long CreationDate { get; init; }

    [JsonPropertyName("CreatedBy")]
    public required string CreatedBy { get; init; }

    /// <summary>UNIX epoch (seconds) when the stack was last updated.</summary>
    [JsonPropertyName("UpdateDate")]
    public long UpdateDate { get; init; }

    [JsonPropertyName("UpdatedBy")]
    public required string UpdatedBy { get; init; }

    // Optional / schemaless sections ----------------------------------------

    [JsonPropertyName("AdditionalFiles")]
    public object? AdditionalFiles { get; init; }

    [JsonPropertyName("AutoUpdate")]
    public object? AutoUpdate { get; init; }

    [JsonPropertyName("Option")]
    public object? Option { get; init; }

    [JsonPropertyName("GitConfig")]
    public object? GitConfig { get; init; }

    [JsonPropertyName("FromAppTemplate")]
    public bool FromAppTemplate { get; init; }

    [JsonPropertyName("Namespace")]
    public string? Namespace { get; init; }
}

/// <summary>
///     One entry inside the <c>Env</c> array.
/// </summary>
public sealed class EnvironmentVariable
{
    [JsonPropertyName("name")]
    public required string Name { get; init; }

    [JsonPropertyName("value")]
    public required string Value { get; init; }
}

/// <summary>
///     Permission wrapper describing who may access the stack.
/// </summary>
public sealed class ResourceControl
{
    [JsonPropertyName("Id")]
    public int Id { get; init; }

    [JsonPropertyName("ResourceId")]
    public required string ResourceId { get; init; }

    [JsonPropertyName("SubResourceIds")]
    public IReadOnlyList<string>? SubResourceIds { get; init; }

    [JsonPropertyName("Type")]
    public int Type { get; init; }

    [JsonPropertyName("UserAccesses")]
    public IReadOnlyList<object>? UserAccesses { get; init; }

    [JsonPropertyName("TeamAccesses")]
    public IReadOnlyList<object>? TeamAccesses { get; init; }

    [JsonPropertyName("Public")]
    public bool Public { get; init; }

    [JsonPropertyName("AdministratorsOnly")]
    public bool AdministratorsOnly { get; init; }

    [JsonPropertyName("System")]
    public bool System { get; init; }
}
