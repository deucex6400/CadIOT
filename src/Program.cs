using Azure.Core.Diagnostics;
using Azure.Identity;
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
                    var appConfigConn = bootstrap["AppConfig:ConnectionString"];
                    var appConfigEndpoint = bootstrap["AppConfig:Endpoint"];

                    // Azure SDK diagnostics
                    _azureSdkListener = new AzureEventSourceListener(
                        (evt, _) =>
                        {
                            var msg = evt.Message ?? string.Empty;
                            if (evt.Payload is { Count: > 0 })
                            {
                                try { msg = string.Format(msg, evt.Payload.ToArray()); } catch { }
                            }
                            Console.WriteLine($"[AZURE SDK] {evt.Level}: [{evt.EventSource?.Name}/{evt.EventName}] {msg}");
                        },
                        System.Diagnostics.Tracing.EventLevel.Informational);

                    // ---- Azure App Configuration wiring ----
                    // Use Managed Identity (system-assigned) to avoid DefaultAzureCredential probing overhead
                    var miCredential = new ManagedIdentityCredential();

                    if (!string.IsNullOrWhiteSpace(appConfigConn))
                    {
                        Console.WriteLine("[APP CONFIG] Connecting via connection string.");
                        config.AddAzureAppConfiguration(o =>
                        {
                            o.Connect(appConfigConn)
                             .Select(KeyFilter.Any)
                             .ConfigureRefresh(r => r
                                 .Register("Sentinels__AppConfigReload", refreshAll: true)
                                 .SetRefreshInterval(TimeSpan.FromSeconds(30)))
                             .UseFeatureFlags(ff => ff.SetRefreshInterval(TimeSpan.FromMinutes(5)));

                            _appConfigRefresher = o.GetRefresher();
                        });
                    }
                    else if (!string.IsNullOrWhiteSpace(appConfigEndpoint))
                    {
                        Console.WriteLine($"[APP CONFIG] Connecting via endpoint: {appConfigEndpoint}");
                        config.AddAzureAppConfiguration(o =>
                        {
                            o.Connect(new Uri(appConfigEndpoint), miCredential)
                             .Select(KeyFilter.Any)
                             .ConfigureRefresh(r => r
                                 .Register("Sentinels__AppConfigReload", refreshAll: true)
                                 .SetRefreshInterval(TimeSpan.FromSeconds(30)))
                             .UseFeatureFlags(ff => ff.SetRefreshInterval(TimeSpan.FromMinutes(5)))
                             .ConfigureKeyVault(kv => kv.SetCredential(miCredential));

                            _appConfigRefresher = o.GetRefresher();
                        });
                    }
                    else
                    {
                        Console.WriteLine("[APP CONFIG] No bootstrap setting found; provider NOT added.");
                    }

                    var effective = config.Build();
                    if (effective is IConfigurationRoot root)
                    {
                        Console.WriteLine("== Configuration Providers ==");
                        foreach (var p in root.Providers) Console.WriteLine($" - {p.GetType().FullName}");
                        Console.WriteLine("=============================");
                    }

                    Console.WriteLine($"[CFG] AppConfig:Endpoint = {effective["AppConfig:Endpoint"]}");
                    Console.WriteLine($"[APP CONFIG] Refresher captured: {(_appConfigRefresher != null ? "yes" : "no")}");
                })
                .ConfigureFunctionsWorkerDefaults()
                .ConfigureServices(services =>
                {
                    services.AddSingleton<cad_dispatch.Services.IoTHubService>();
                    services.AddSingleton<cad_dispatch.Services.AuditLogService>();

                    // Use MI for Graph client as well
                    services.AddSingleton<cad_dispatch.Services.GraphClientFactory>(_ =>
                    {
                        var credential = new ManagedIdentityCredential();
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
