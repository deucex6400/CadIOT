
using System;
using System.Threading.Tasks;
using Azure.Core.Diagnostics;
using Azure.Identity;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Configuration.AzureAppConfiguration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Graph;

namespace cad_dispatch
{
    public class Program
    {
        private static IConfigurationRefresher? _appConfigRefresher;
        private static AzureEventSourceListener? _azureSdkListener;

        public static async Task Main(string[] args)
        {
            var host = new HostBuilder()
                .ConfigureAppConfiguration((ctx, config) =>
                {
                    // Base providers: JSON + ENV
                    config.AddJsonFile("appsettings.json", optional: true, reloadOnChange: true)
                          .AddJsonFile($"appsettings.{ctx.HostingEnvironment.EnvironmentName}.json", optional: true, reloadOnChange: true)
                          .AddEnvironmentVariables();

                    // Bootstrap
                    var bootstrap = config.Build();
                    var appConfigConn = bootstrap["AppConfig__ConnectionString"];
                    var appConfigEndpoint = bootstrap["AppConfig__Endpoint"];

                    // Azure SDK diagnostics
                    _azureSdkListener = new AzureEventSourceListener(
                        (e, _) => Console.WriteLine($"[AZURE SDK] {e.Level}: {e.Message}"),
                        System.Diagnostics.Tracing.EventLevel.Informational);

                    if (!string.IsNullOrWhiteSpace(appConfigConn))
                    {
                        Console.WriteLine("[APP CONFIG] Connecting via connection string.");
                        config.AddAzureAppConfiguration(o =>
                        {
                            o.Connect(appConfigConn)
                             .Select(KeyFilter.Any) // label-free
                             .ConfigureRefresh(r => r.Register("Sentinels__AppConfigReload", refreshAll: true)
                                                     .SetRefreshInterval(TimeSpan.FromSeconds(30)))
                             .UseFeatureFlags(ff => ff.SetRefreshInterval(TimeSpan.FromMinutes(5)));
                            _appConfigRefresher = o.GetRefresher();
                        });
                    }
                    else if (!string.IsNullOrWhiteSpace(appConfigEndpoint))
                    {
                        Console.WriteLine($"[APP CONFIG] Connecting via endpoint: {appConfigEndpoint}");
                        var cred = new DefaultAzureCredential();
                        config.AddAzureAppConfiguration(o =>
                        {
                            o.Connect(new Uri(appConfigEndpoint), cred)
                             .Select(KeyFilter.Any) // label-free
                             .ConfigureRefresh(r => r.Register("Sentinels__AppConfigReload", refreshAll: true)
                                                     .SetRefreshInterval(TimeSpan.FromSeconds(30)))
                             .UseFeatureFlags(ff => ff.SetRefreshInterval(TimeSpan.FromMinutes(5)))
                             .ConfigureKeyVault(kv => kv.SetCredential(cred));
                            _appConfigRefresher = o.GetRefresher();
                        });
                    }
                    else
                    {
                        Console.WriteLine("[APP CONFIG] No AppConfig bootstrap setting found (AppConfig__ConnectionString or AppConfig__Endpoint). Provider NOT added.");
                    }

                    // Build only for diagnostics (do not assign back)
                    var effective = config.Build();
                    if (effective is IConfigurationRoot root)
                    {
                        Console.WriteLine("== Configuration Providers ==");
                        foreach (var p in root.Providers)
                            Console.WriteLine($" - {p.GetType().FullName}");
                        Console.WriteLine("=============================");
                    }

                    Console.WriteLine($"[CFG] AppConfig__Endpoint       = {effective["AppConfig__Endpoint"]}");
                    Console.WriteLine($"[CFG] Storage:TableName         = {effective["Storage:TableName"]}");
                    Console.WriteLine($"[CFG] Storage:AccountUri        = {effective["Storage:AccountUri"]}");
                    Console.WriteLine($"[CFG] Storage:ConnectionString  = {(string.IsNullOrWhiteSpace(effective["Storage:ConnectionString"]) ? "(null)" : "(present)")}");
                    Console.WriteLine($"[CFG] IoTHub:ConnectionString   = {(string.IsNullOrWhiteSpace(effective["IoTHub:ConnectionString"]) ? "(null)" : "(present)")}");
                    Console.WriteLine($"[APP CONFIG] Refresher captured: {(_appConfigRefresher != null ? "yes" : "no")}");
                })
                .ConfigureFunctionsWorkerDefaults() // keep worker defaults: env + gRPC
                .ConfigureServices(services =>
                {
                    // Do not override IConfiguration
                    services.AddSingleton<cad_dispatch.Services.IoTHubService>();
                    services.AddSingleton<cad_dispatch.Services.AuditLogService>();
                    services.AddSingleton<cad_dispatch.Services.GraphClientFactory>(_ =>
                    {
                        var credential = new DefaultAzureCredential();
                        var graphClient = new GraphServiceClient(credential);
                        return new cad_dispatch.Services.GraphClientFactory(graphClient);
                    });

                    if (_appConfigRefresher != null)
                        services.AddSingleton(_appConfigRefresher);
                })
                .Build();

            // Optional periodic refresh
            _ = Task.Run(async () =>
            {
                while (true)
                {
                    try
                    {
                        if (_appConfigRefresher != null)
                        {
                            await _appConfigRefresher.TryRefreshAsync();
                            Console.WriteLine($"[APP CONFIG] TryRefreshAsync invoked @ {DateTime.UtcNow:O}");
                        }
                    }
                    catch (Exception ex)
                    {
                        Console.Error.WriteLine($"[APP CONFIG] Refresh error: {ex.Message}");
                    }
                    await Task.Delay(TimeSpan.FromSeconds(30));
                }
            });

            await host.RunAsync();
        }
    }
}