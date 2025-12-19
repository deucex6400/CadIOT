
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
        // Base providers
        config.AddJsonFile("appsettings.json", optional: true, reloadOnChange: true)
              .AddJsonFile($"appsettings.{ctx.HostingEnvironment.EnvironmentName}.json", optional: true, reloadOnChange: true)
              .AddEnvironmentVariables();

        // Read bootstrap settings (env/local.settings.json)
        var bootstrap = config.Build();
        var appConfigConn = bootstrap["AppConfig__ConnectionString"];
        var appConfigEndpoint = bootstrap["AppConfig__Endpoint"];

        if (!string.IsNullOrWhiteSpace(appConfigConn))
        {
            // Connect via connection string (label-free)
            config.AddAzureAppConfiguration(options =>
            {
                options.Connect(appConfigConn)
                       // Load all keys, regardless of label
                       .Select(KeyFilter.Any)
                       // Refresh: register a sentinel key (no label dependency)
                       .ConfigureRefresh(refresh =>
                           refresh.Register("Sentinels__AppConfigReload", refreshAll: true)
                                  .SetRefreshInterval(TimeSpan.FromSeconds(30)))
                       // Feature flags without labels
                       .UseFeatureFlags(ff =>
                       {
                           ff.SetRefreshInterval(TimeSpan.FromMinutes(5));
                           // No ff.Label assignment -> use default/no-label feature flags
                       });
            });
        }
        else if (!string.IsNullOrWhiteSpace(appConfigEndpoint))
        {
            var cred = new DefaultAzureCredential();
            // Connect via endpoint + MI (label-free)
            config.AddAzureAppConfiguration(options =>
            {
                options.Connect(new Uri(appConfigEndpoint), cred)
                       .Select(KeyFilter.Any)
                       .ConfigureRefresh(refresh =>
                           refresh.Register("Sentinels__AppConfigReload", refreshAll: true)
                                  .SetRefreshInterval(TimeSpan.FromSeconds(30)))
                       .UseFeatureFlags(ff =>
                       {
                           ff.SetRefreshInterval(TimeSpan.FromMinutes(5));
                           // No ff.Label assignment
                       })
                       .ConfigureKeyVault(kv => kv.SetCredential(cred));
            });
        }

        // Build AFTER App Config provider is added
        var effective = config.Build();
        Console.WriteLine($"[CFG] Storage:TableName        = {effective["Storage:TableName"]}");
        Console.WriteLine($"[CFG] Storage:AccountUri       = {effective["Storage:AccountUri"]}");
        Console.WriteLine($"[CFG] Storage:ConnectionString = {effective["Storage:ConnectionString"]}");
        Console.WriteLine($"[CFG] IoTHub:HostName          = {effective["IoTHub:HostName"]}");
        Console.WriteLine($"[CFG] IoTHub:ConnectionString  = {effective["IoTHub:ConnectionString"]}");
        Console.WriteLine($"[CFG] IoTHub__ConnectionString = {effective["IoTHub__ConnectionString"]}");

        // Use 'effective' for validation (includes App Config)
        if (string.IsNullOrWhiteSpace(effective["Storage:AccountUri"]) &&
            string.IsNullOrWhiteSpace(effective["Storage:ConnectionString"]))
        {
            Console.Error.WriteLine("[CFG] ERROR: Storage settings missing. Set Storage:AccountUri or Storage:ConnectionString in Azure App Configuration.");
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