
using System;
using Azure.Identity;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Configuration.AzureAppConfiguration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Graph;
using cad_dispatch.Services;
using GraphClientFactory = cad_dispatch.Services.GraphClientFactory;

var host = new HostBuilder()
    .ConfigureAppConfiguration((ctx, config) =>
    {
        config.AddJsonFile("appsettings.json", optional: true, reloadOnChange: true)
              .AddJsonFile($"appsettings.{ctx.HostingEnvironment.EnvironmentName}.json", optional: true, reloadOnChange: true)
              .AddEnvironmentVariables();

        var built = config.Build();
        var appConfigConn = built["AppConfig__ConnectionString"];
        var appConfigEndpoint = built["AppConfig__Endpoint"];

        if (!string.IsNullOrWhiteSpace(appConfigConn))
        {
            config.AddAzureAppConfiguration(options =>
            {
                options.Connect(appConfigConn)
                       .Select(KeyFilter.Any, LabelFilter.Null) // base (no-label)
                       .Select(KeyFilter.Any, "prod")           // overlay prod label
                       .ConfigureRefresh(refresh =>
                           refresh.Register("Sentinels__AppConfigReload", refreshAll: true)
                                  .SetRefreshInterval(TimeSpan.FromSeconds(30)))
                       .UseFeatureFlags(ff =>
                       {
                           ff.SetRefreshInterval(TimeSpan.FromMinutes(5));
                           ff.Label = "prod";
                       });
            });
        }
        else if (!string.IsNullOrWhiteSpace(appConfigEndpoint))
        {
            var cred = new DefaultAzureCredential();
            config.AddAzureAppConfiguration(options =>
            {
                options.Connect(new Uri(appConfigEndpoint), cred)
                       .Select(KeyFilter.Any, LabelFilter.Null)
                       .Select(KeyFilter.Any, "prod")
                       .ConfigureRefresh(refresh =>
                           refresh.Register("Sentinels__AppConfigReload", refreshAll: true)
                                  .SetRefreshInterval(TimeSpan.FromSeconds(30)))
                       .UseFeatureFlags(ff =>
                       {
                           ff.SetRefreshInterval(TimeSpan.FromMinutes(5));
                           ff.Label = "prod";
                       })
                       .ConfigureKeyVault(kv => kv.SetCredential(cred));
            });
        }
    })
    .ConfigureFunctionsWorkerDefaults()
    .ConfigureServices(services =>
    {
        services.AddSingleton<IoTHubService>();
        services.AddSingleton<AuditLogService>();
        services.AddSingleton<GraphClientFactory>(_ =>
        {
            var credential = new DefaultAzureCredential();
            var graphClient = new GraphServiceClient(credential);
            return new GraphClientFactory(graphClient);
        });
    })
    .Build();

await host.RunAsync();