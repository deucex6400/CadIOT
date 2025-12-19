
// Program.cs (for .NET 8 Azure Functions isolated worker)
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
using cad_dispatch.Services;
using GraphClientFactory = cad_dispatch.Services.GraphClientFactory;

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
                    // Base providers: local files + environment
                    config.AddJsonFile("appsettings.json", optional: true, reloadOnChange: true)
                          .AddJsonFile($"appsettings.{ctx.HostingEnvironment.EnvironmentName}.json", optional: true, reloadOnChange: true)
                          .AddEnvironmentVariables();

                    // Bootstrap to read AppConfig settings from env/appsettings
                    var bootstrap = config.Build();
                    var appConfigConn = bootstrap["AppConfig__ConnectionString"];
                    var appConfigEndpoint = bootstrap["AppConfig__Endpoint"];

                    // Enable Azure SDK diagnostics to surface identity/client events
                    _azureSdkListener = new AzureEventSourceListener(
                        (args, _) => Console.WriteLine($"[AZURE SDK] {args.Level}: {args.Message}"),
                        System.Diagnostics.Tracing.EventLevel.Informational);

                    if (!string.IsNullOrWhiteSpace(appConfigConn))
                    {
                        Console.WriteLine("[APP CONFIG] Connecting via connection string.");
                        config.AddAzureAppConfiguration(options =>
                        {
                            options.Connect(appConfigConn)
                                   // Label-free: load all keys
                                   .Select(KeyFilter.Any)
                                   .ConfigureRefresh(r => r.Register("Sentinels__AppConfigReload", refreshAll: true)
                                                           .SetRefreshInterval(TimeSpan.FromSeconds(30)))
                                   .UseFeatureFlags(ff => ff.SetRefreshInterval(TimeSpan.FromMinutes(5)));

                            _appConfigRefresher = options.GetRefresher();
                        });
                    }
                    else if (!string.IsNullOrWhiteSpace(appConfigEndpoint))
                    {
                        Console.WriteLine($"[APP CONFIG] Connecting via endpoint: {appConfigEndpoint}");
                        var cred = new DefaultAzureCredential();
                        config.AddAzureAppConfiguration(options =>
                        {
                            options.Connect(new Uri(appConfigEndpoint), cred)
                                   .Select(KeyFilter.Any) // label-free
                                   .ConfigureRefresh(r => r.Register("Sentinels__AppConfigReload", refreshAll: true)
                                                           .SetRefreshInterval(TimeSpan.FromSeconds(30)))
                                   .UseFeatureFlags(ff => ff.SetRefreshInterval(TimeSpan.FromMinutes(5)))
                                   .ConfigureKeyVault(kv => kv.SetCredential(cred));

                            _appConfigRefresher = options.GetRefresher();
                        });
                    }
                    else
                    {
                        Console.WriteLine("[APP CONFIG] No AppConfig bootstrap setting found (AppConfig__ConnectionString or AppConfig__Endpoint). Provider NOT added.");
                    }

                    // Build AFTER adding App Config to inspect effective values
                    var effective = config.Build();

                    // Enumerate providers to prove AzureAppConfiguration provider is loaded
                    if (effective is IConfigurationRoot root)
                    {
                        Console.WriteLine("== Configuration Providers ==");
                        foreach (var p in root.Providers)
                            Console.WriteLine($" - {p.GetType().FullName}");
                        Console.WriteLine("=============================");
                    }

                    // Key probes (avoid printing secret values themselves)
                    Console.WriteLine($"[CFG] Storage:TableName        = {effective["Storage:TableName"]}");
                    Console.WriteLine($"[CFG] Storage:AccountUri       = {effective["Storage:AccountUri"]}");
                    Console.WriteLine($"[CFG] Storage:ConnectionString = {(string.IsNullOrWhiteSpace(effective["Storage:ConnectionString"]) ? "(null)" : "(present)")}");
                    Console.WriteLine($"[CFG] IoTHub:ConnectionString  = {(string.IsNullOrWhiteSpace(effective["IoTHub:ConnectionString"]) ? "(null)" : "(present)")}");
                    Console.WriteLine($"[APP CONFIG] Refresher captured: {(_appConfigRefresher != null ? "yes" : "no")}");

                    // Guardrail: warn if core storage values are missing
                    if (string.IsNullOrWhiteSpace(effective["Storage:AccountUri"]) &&
                        string.IsNullOrWhiteSpace(effective["Storage:ConnectionString"]))
                    {
                        Console.Error.WriteLine("[CFG] WARNING: Storage settings missing. Set Storage:AccountUri or Storage:ConnectionString in Azure App Configuration or Application settings.");
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

                    // Expose refresher via DI if you want to trigger refresh from functions/services
                    if (_appConfigRefresher != null)
                        services.AddSingleton(_appConfigRefresher);
                })
                .Build();

            // Optional periodic refresh loop (background task) to prove refresh works
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
