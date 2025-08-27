// Program.cs
using Docker.DotNet;
using DockerUpdateService.Options;
using DockerUpdateService.Services;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

var builder = Host.CreateApplicationBuilder(args);

// Logging (simple console, timestamps)
builder.Logging.ClearProviders();
builder.Logging.AddSimpleConsole(o =>
{
    o.TimestampFormat = "yyyy-MM-dd HH:mm:ss ";
    o.SingleLine = true;
});

// Options from environment variables
builder.Services.AddSingleton(UpdateOptions.LoadFromEnvironment());
builder.Services.AddSingleton(PortainerOptions.LoadFromEnvironment());

// Docker client
builder.Services.AddSingleton<IDockerClient>(sp =>
{
    var logger = sp.GetRequiredService<ILoggerFactory>().CreateLogger("DockerClient");
    var dockerHost = Environment.GetEnvironmentVariable("DOCKER_HOST");

    string? uri = dockerHost switch
    {
        not null and not "" => dockerHost,
        _ => OperatingSystem.IsWindows()
                ? "npipe://./pipe/docker_engine"
                : (File.Exists("/var/run/docker.sock") ? "unix:///var/run/docker.sock" : null)
    };

    if (uri is null)
        throw new InvalidOperationException("Could not locate Docker engine. Mount /var/run/docker.sock or set DOCKER_HOST.");

    logger.LogInformation("Connecting to Docker: {Uri}", uri);
    return new DockerClientConfiguration(new Uri(uri)).CreateClient();
});

// HttpClient for Portainer
builder.Services.AddHttpClient<PortainerService>()
    .ConfigureHttpClient((sp, client) =>
    {
        var opt = sp.GetRequiredService<PortainerOptions>();
        if (!string.IsNullOrWhiteSpace(opt.Url))
        {
            client.BaseAddress = new Uri(opt.Url!);
        }
        if (!string.IsNullOrWhiteSpace(opt.ApiKey))
        {
            client.DefaultRequestHeaders.TryAddWithoutValidation("X-API-Key", opt.ApiKey);
        }
    })
    .ConfigurePrimaryHttpMessageHandler(sp =>
    {
        var opt = sp.GetRequiredService<PortainerOptions>();
        return new HttpClientHandler
        {
            ServerCertificateCustomValidationCallback =
                opt.InsecureTls ? HttpClientHandler.DangerousAcceptAnyServerCertificateValidator : null
        };
    });

// Core services
builder.Services.AddSingleton<DockerEngineService>();
builder.Services.AddHostedService<DockerUpdateWorker>();

var app = builder.Build();

await app.RunAsync();
