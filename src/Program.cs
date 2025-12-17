using Azure.Identity;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Azure.Functions.Worker.Configuration;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Graph;
using cad_dispatch.Services;
using GraphClientFactory = cad_dispatch.Services.GraphClientFactory;

// Fix: Replace ConfigureFunctionsWebApplication with ConfigureFunctionsWorkerDefaults
var host = new HostBuilder()
    .ConfigureAppConfiguration((ctx, config) =>
    {
        var built = config.Build();
        var appConfigConn = built["AppConfig__ConnectionString"]; // connection string option
        var appConfigEndpoint = built["AppConfig__Endpoint"];     // MSI option (e.g., https://<appconfig>.azconfig.io)
        if (!string.IsNullOrEmpty(appConfigConn))
        {
            config.AddAzureAppConfiguration(appConfigConn);
        }
        else if (!string.IsNullOrEmpty(appConfigEndpoint))
        {
            var cred = new DefaultAzureCredential();
            config.AddAzureAppConfiguration(options =>
                options.Connect(new Uri(appConfigEndpoint), cred)
                       .ConfigureRefresh(refresh =>
                       {
                           // Optional sentinel to trigger full refresh when its value changes
                           refresh.Register("Sentinels__AppConfigReload", refreshAll: true)
                                  .SetRefreshInterval(TimeSpan.FromSeconds(30));
                       })
                       .UseFeatureFlags());
        }
    })
    .ConfigureFunctionsWorkerDefaults() // Updated method to fix CS1061
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

host.Run();
