namespace DockerUpdateService.Models;

public sealed record PortainerStack(int Id, string Name, int EndpointId);

public sealed record StackEnv(string Name, string? Value);

public sealed record StackFileResponse(string StackFileContent);
