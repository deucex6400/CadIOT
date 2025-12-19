using cad_dispatch.Services;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Graph.Models;

namespace cad_dispatch.Functions
{
    public class SubscriptionManager
    {
        private readonly GraphClientFactory _graphFactory;
        private readonly IConfiguration _config;
        private readonly AuditLogService _audit;
        private readonly ILogger<SubscriptionManager> _log;

        public SubscriptionManager(GraphClientFactory graphFactory, IConfiguration config, AuditLogService audit, ILogger<SubscriptionManager> log)
        { _graphFactory = graphFactory; _config = config; _audit = audit; _log = log; }

        private string? Get(string colonKey, string underscoreKey) => _config[colonKey] ?? _config[underscoreKey];

        [Function("SubscriptionManager")]
        public async Task Run([TimerTrigger("0 */30 * * * *")] TimerInfo timerInfo)
        {
            var graph = _graphFactory.Client;
            var mailbox = Get("Dispatch:SharedMailbox", "Dispatch__SharedMailbox");
            var webhookUrl = Get("Dispatch:WebhookUrl", "Dispatch__WebhookUrl");
            var lifecycleUrl = Get("Dispatch:LifecycleWebhookUrl", "Dispatch__LifecycleWebhookUrl");
            var useRichRaw = Get("Dispatch:UseRichNotifications", "Dispatch__UseRichNotifications");
            var encCertBase64 = Get("Dispatch:EncryptionCertBase64", "Dispatch__EncryptionCertBase64");
            var encCertId = Get("Dispatch:EncryptionCertId", "Dispatch__EncryptionCertId") ?? "bgvfd-cert";
            var useRich = bool.TryParse(useRichRaw, out var b) && b;

            _log.LogInformation("SubscriptionManager starting. mailbox={Mailbox}, webhookUrl={Webhook}", mailbox, webhookUrl);
            if (string.IsNullOrWhiteSpace(mailbox) || string.IsNullOrWhiteSpace(webhookUrl))
            { _log.LogWarning("SubscriptionManager missing mailbox or webhookUrl."); await _audit.WriteAsync("subscription_config_missing", e => { }); return; }
            if (!TryValidateHttpsUrl(webhookUrl, out _))
            { _log.LogError("Invalid Dispatch:WebhookUrl value: '{Url}'", webhookUrl); await _audit.WriteAsync("subscription_config_invalid", e => { e["webhookUrl"] = webhookUrl; }); return; }
            Uri? lifecycleUri = null;
            if (!string.IsNullOrWhiteSpace(lifecycleUrl) && !TryValidateHttpsUrl(lifecycleUrl!, out lifecycleUri)) { _log.LogWarning("LifecycleWebhookUrl invalid; ignoring: '{Url}'", lifecycleUrl); lifecycleUrl = null; }

            var resourceBasic = $"/users/{mailbox}/mailFolders('inbox')/messages";
            var desiredLifetimeMinutes = useRich ? 1440 : 4320; // conservative lifetime; Graph enforces limits per resource

            var existingSubs = await graph.Subscriptions.GetAsync();
            _log.LogInformation("Existing subscriptions count: {Count}", existingSubs?.Value?.Count ?? 0);
            var match = existingSubs?.Value?.FirstOrDefault(s =>
                string.Equals(Normalize(s.Resource), Normalize(resourceBasic), StringComparison.OrdinalIgnoreCase) &&
                string.Equals(s.NotificationUrl, webhookUrl, StringComparison.OrdinalIgnoreCase));

            if (match is null)
            {
                var sub = new Subscription
                {
                    ChangeType = "created",
                    Resource = resourceBasic,
                    NotificationUrl = webhookUrl,
                    ClientState = "bgvfd-alerts",
                    ExpirationDateTime = DateTimeOffset.UtcNow.AddMinutes(desiredLifetimeMinutes)
                };
                if (!string.IsNullOrEmpty(lifecycleUrl)) sub.LifecycleNotificationUrl = lifecycleUrl;
                if (useRich && !string.IsNullOrWhiteSpace(encCertBase64))
                {
                    sub.IncludeResourceData = true;
                    sub.EncryptionCertificate = encCertBase64;
                    sub.EncryptionCertificateId = encCertId;
                    sub.Resource = $"/users/{mailbox}/mailFolders('inbox')/messages?$select=subject,from,receivedDateTime";
                }
                _log.LogInformation("Creating Graph subscription for resource={Resource} webhook={Webhook}", sub.Resource, sub.NotificationUrl);
                match = await graph.Subscriptions.PostAsync(sub);
                if (match is null) { _log.LogWarning("Created Graph subscription is null"); return; }
                _log.LogInformation("Created Graph subscription {Id} exp {Exp}", match.Id ?? "(null)", match.ExpirationDateTime?.ToString("O") ?? "(null)");
                await _audit.WriteAsync("subscription_created", e => { e["subscriptionId"] = match.Id; e["expires"] = match.ExpirationDateTime; e["webhookUrl"] = webhookUrl; });
                return;
            }

            var threshold = TimeSpan.FromMinutes(useRich ? 30 : 120);
            if (match.ExpirationDateTime.HasValue && match.ExpirationDateTime.Value - DateTimeOffset.UtcNow < threshold)
            {
                var update = new Subscription { ExpirationDateTime = DateTimeOffset.UtcNow.AddMinutes(desiredLifetimeMinutes) };
                if (useRich && !string.IsNullOrWhiteSpace(encCertBase64))
                {
                    update.IncludeResourceData = true;
                    update.EncryptionCertificate = encCertBase64;
                    update.EncryptionCertificateId = encCertId;
                    update.Resource = $"/users/{mailbox}/mailFolders('inbox')/messages?$select=subject,from,receivedDateTime";
                }
                _log.LogInformation("Renewing Graph subscription {Id} new exp {Exp}", match.Id, update.ExpirationDateTime);
                await graph.Subscriptions[match.Id].PatchAsync(update);
                await _audit.WriteAsync("subscription_renewed", e => { e["subscriptionId"] = match.Id; e["expires"] = update.ExpirationDateTime; e["webhookUrl"] = webhookUrl; });
            }
            else
            {
                await _audit.WriteAsync("subscription_ok", e => { e["subscriptionId"] = match.Id; e["expires"] = match.ExpirationDateTime; e["webhookUrl"] = webhookUrl; });
            }
        }

        private static bool TryValidateHttpsUrl(string url, out Uri uri)
        { uri = default!; if (!Uri.TryCreate(url, UriKind.Absolute, out var parsed)) return false; if (!string.Equals(parsed.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase)) return false; if (string.IsNullOrWhiteSpace(parsed.Host)) return false; uri = parsed; return true; }
        private static string Normalize(string? resource)
        { if (string.IsNullOrWhiteSpace(resource)) return string.Empty; var idx = resource.IndexOf("/messages", StringComparison.OrdinalIgnoreCase); if (idx >= 0) return resource.Substring(0, idx + "/messages".Length); return resource.Trim(); }
    }
}