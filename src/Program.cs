
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

                    // Bootstrap (read colon keys from IConfiguration)
                    var bootstrap = config.Build();
                    var appConfigConn = bootstrap["AppConfig:ConnectionString"];
                    var appConfigEndpoint = bootstrap["AppConfig:Endpoint"];

                    // ---- Payload-aware Azure SDK diagnostics listener ----
                    _azureSdkListener = new AzureEventSourceListener(
                        (evt, _) =>
                        {
                            var level = evt.Level;
                            var message = evt.Message ?? string.Empty;

                            // Try formatting the template message with payloads to avoid {0} {1} placeholders
                            if (evt.Payload is not null && evt.Payload.Count > 0)
                            {
                                try
                                {
                                    message = string.Format(message, evt.Payload.ToArray());
                                }
                                catch
                                {
                                    // Fall back to raw template if formatting fails
                                }
                            }

                            var sourceName = evt.EventSource?.Name ?? "AzureSDK";
                            Console.WriteLine($"[AZURE SDK] {level}: [{sourceName}/{evt.EventName}] {message}");
                        },
                        // Change to EventLevel.Warning if you want fewer logs
                        System.Diagnostics.Tracing.EventLevel.Informational
                    );

                    // ---- Azure App Configuration provider wiring ----
                    if (!string.IsNullOrWhiteSpace(appConfigConn))
                    {
                        Console.WriteLine("[APP CONFIG] Connecting via connection string.");
                        config.AddAzureAppConfiguration(o =>
                        {
                            o.Connect(appConfigConn)
                             .Select(KeyFilter.Any) // label-free
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
                        var cred = new DefaultAzureCredential();
                        config.AddAzureAppConfiguration(o =>
                        {
                            o.Connect(new Uri(appConfigEndpoint), cred)
                             .Select(KeyFilter.Any) // label-free
                             .ConfigureRefresh(r => r
                                 .Register("Sentinels__AppConfigReload", refreshAll: true)
                                 .SetRefreshInterval(TimeSpan.FromSeconds(30)))
                             .UseFeatureFlags(ff => ff.SetRefreshInterval(TimeSpan.FromMinutes(5)))
                             .ConfigureKeyVault(kv => kv.SetCredential(cred));
                            _appConfigRefresher = o.GetRefresher();
                        });
                    }
                    else
                    {
                        Console.WriteLine("[APP CONFIG] No AppConfig bootstrap setting found (AppConfig:ConnectionString or AppConfig:Endpoint). Provider NOT added.");
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

                    // Resolved key diagnostics
                    Console.WriteLine($"[CFG] AppConfig:Endpoint = {effective["AppConfig:Endpoint"]}");
                    Console.WriteLine($"[CFG] Storage:TableName = {effective["Storage:TableName"]}");
                    Console.WriteLine($"[CFG] Storage:AccountUri = {effective["Storage:AccountUri"]}");
                    Console.WriteLine($"[CFG] Storage:ConnectionString = {(string.IsNullOrWhiteSpace(effective["Storage:ConnectionString"]) ? "(null)" : "(present)")}");
                    Console.WriteLine($"[CFG] IoTHub:ConnectionString = {(string.IsNullOrWhiteSpace(effective["IoTHub:ConnectionString"]) ? "(null)" : "(present)")}");

                    // NEW: log resolved Dispatch keys to help diagnose webhook issues
                    var dispatchWebhook = effective["Dispatch:WebhookUrl"] ?? effective["Dispatch__WebhookUrl"];
                    var dispatchMailbox = effective["Dispatch:SharedMailbox"] ?? effective["Dispatch__SharedMailbox"];
                    var lifecycleWebhook = effective["Dispatch:LifecycleWebhookUrl"] ?? effective["Dispatch__LifecycleWebhookUrl"];
                    Console.WriteLine($"[CFG] Dispatch:SharedMailbox = {dispatchMailbox}");
                    Console.WriteLine($"[CFG] Dispatch:WebhookUrl = {dispatchWebhook}");
                    Console.WriteLine($"[CFG] Dispatch:LifecycleWebhookUrl = {lifecycleWebhook}");

                    // Simple validation hints (console-only; SubscriptionManager does strong checks)
                    if (!string.IsNullOrWhiteSpace(dispatchWebhook))
                    {
                        if (!Uri.TryCreate(dispatchWebhook, UriKind.Absolute, out var u) ||
                            !string.Equals(u.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase) ||
                            string.IsNullOrWhiteSpace(u.Host))
                        {
                            Console.Error.WriteLine($"[CFG] WARNING: Dispatch:WebhookUrl appears invalid: '{dispatchWebhook}'. Expected absolute HTTPS URL with non-empty host.");
                        }
                    }

                    Console.WriteLine($"[APP CONFIG] Refresher captured: {(_appConfigRefresher != null ? "yes" : "no")}");
                })
                .ConfigureFunctionsWorkerDefaults() // keep worker defaults: env + gRPC (do not replace host config)
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