using Docker.DotNet;
using DockerUpdateService.Options;
using DockerUpdateService.Services;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using System.Runtime.InteropServices;

Host.CreateDefaultBuilder(args)
    .ConfigureServices((ctx, services) =>
    {
        // ---------- Options ---------------------------------------------------
        services.AddOptions<UpdateSettings>()
                .Bind(ctx.Configuration.GetSection(UpdateSettings.Section))
                .ValidateDataAnnotations()   
                .ValidateOnStart();          

        services.AddOptions<SchedulingSettings>()
                .Bind(ctx.Configuration.GetSection(SchedulingSettings.Section))
                .ValidateOnStart();

        // ---------- Docker ----------------------------------------------------
        services.AddSingleton<IDockerClient>(_ =>
        {
            var uri = RuntimeInformation.IsOSPlatform(OSPlatform.Windows)
                ? "npipe://./pipe/docker_engine"
                : "unix:///var/run/docker.sock";
            return new DockerClientConfiguration(new Uri(uri)).CreateClient();
        });

        // ---------- Portainer HTTP client -------------------------------------
        services.AddHttpClient<IPortainerClient, PortainerClient>()
        .ConfigureHttpClient((sp, http) =>
        {
            var cfg = sp.GetRequiredService<IOptions<UpdateSettings>>().Value.Portainer;
            if (string.IsNullOrWhiteSpace(cfg?.Url) || string.IsNullOrWhiteSpace(cfg.ApiKey))
                throw new InvalidOperationException("Portainer disabled: Url or ApiKey missing");
            http.BaseAddress = new Uri(cfg.Url);
            http.DefaultRequestHeaders.Add("X-API-Key", cfg.ApiKey);
        });

        // ---------- Core services --------------------------------------------
        services.AddSingleton<IStackUpdater, StackUpdater>();
        services.AddSingleton<IContainerUpdater, ContainerUpdater>();
        services.AddSingleton<IPruner, Pruner>();

        services.AddHostedService<UpdateWorker>();
    })
    .Build()
    .Run();
