// Program.cs (full, label-free, with diagnostics)
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
                    config.AddJsonFile("appsettings.json", optional: true, reloadOnChange: true)
                          .AddJsonFile($"appsettings.{ctx.HostingEnvironment.EnvironmentName}.json", optional: true, reloadOnChange: true)
                          .AddEnvironmentVariables();

                    var bootstrap = config.Build();
                    var appConfigConn     = bootstrap["AppConfig__ConnectionString"];
                    var appConfigEndpoint = bootstrap["AppConfig__Endpoint"];

                    _azureSdkListener = new AzureEventSourceListener(
                        (args, _) => Console.WriteLine($"[AZURE SDK] {args.Level}: {args.Message}"),
                        System.Diagnostics.Tracing.EventLevel.Informational);

                    if (!string.IsNullOrWhiteSpace(appConfigConn))
                    {
                        Console.WriteLine("[APP CONFIG] Connecting via connection string.");
                        config.AddAzureAppConfiguration(options =>
                        {
                            options.Connect(appConfigConn)
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
                                   .Select(KeyFilter.Any)
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

                    var effective = config.Build();
                    if (effective is IConfigurationRoot root)
                    {
                        Console.WriteLine("== Configuration Providers ==");
                        foreach (var p in root.Providers)
                            Console.WriteLine($" - {p.GetType().FullName}");
                        Console.WriteLine("=============================");
                    }

                    Console.WriteLine($"[CFG] Storage:TableName        = {effective["Storage:TableName"]}");
                    Console.WriteLine($"[CFG] Storage:AccountUri       = {effective["Storage:AccountUri"]}");
                    Console.WriteLine($"[CFG] Storage:ConnectionString = {(string.IsNullOrWhiteSpace(effective["Storage:ConnectionString"]) ? "(null)" : "(present)")}");
                    Console.WriteLine($"[CFG] IoTHub:ConnectionString  = {(string.IsNullOrWhiteSpace(effective["IoTHub:ConnectionString"]) ? "(null)" : "(present)")}");
                    Console.WriteLine($"[APP CONFIG] Refresher captured: {( _appConfigRefresher != null ? "yes" : "no" )}");

                    if (string.IsNullOrWhiteSpace(effective["Storage:AccountUri"]) &&
                        string.IsNullOrWhiteSpace(effective["Storage:ConnectionString"]))
                    {
                        Console.Error.WriteLine("[CFG] WARNING: Storage settings missing. Set Storage:AccountUri or Storage:ConnectionString in Azure App Configuration or Application settings.");
                    }
                })
                .ConfigureFunctionsWorkerDefaults()
                .ConfigureServices(services =>
                {
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
