using Docker.DotNet;
using DockerUpdateService.Options;
using DockerUpdateService.Services;
using Microsoft.Extensions.Configuration;
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

        // Conditionally register Portainer integration
        var updCfg = ctx.Configuration.GetSection(UpdateSettings.Section).Get<UpdateSettings>();
        if (!string.IsNullOrWhiteSpace(updCfg?.Portainer?.Url) &&
            !string.IsNullOrWhiteSpace(updCfg.Portainer.ApiKey))
        {
            services.AddHttpClient<IPortainerClient, PortainerClient>()
                    .ConfigureHttpClient((sp, http) =>
                    {
                        var cfg = sp.GetRequiredService<IOptions<UpdateSettings>>().Value.Portainer!;
                        http.BaseAddress = new Uri(cfg.Url!);
                        http.DefaultRequestHeaders.Add("X-API-Key", cfg.ApiKey!);
                    });
        }
        else
        {
            services.AddSingleton<IPortainerClient, NullPortainerClient>();
        }

        // ---------- Core services --------------------------------------------
        services.AddSingleton<IStackUpdater, StackUpdater>();
        services.AddSingleton<IContainerUpdater, ContainerUpdater>();
        services.AddSingleton<IPruner, Pruner>();

        services.AddHostedService<UpdateWorker>();
    })
    .Build()
    .Run();
