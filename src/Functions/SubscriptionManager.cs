using System;
using System.Linq;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Graph.Models;
using cad_dispatch.Services;

namespace cad_dispatch.Functions
{
    public class SubscriptionManager
    {
        private readonly GraphClientFactory _graphFactory;
        private readonly IConfiguration _config;
        private readonly AuditLogService _audit;
        private readonly ILogger<SubscriptionManager> _log;

        public SubscriptionManager(GraphClientFactory graphFactory, IConfiguration config, AuditLogService audit, ILogger<SubscriptionManager> log)
        {
            _graphFactory = graphFactory;
            _config = config;
            _audit = audit;
            _log = log;
        }

        [Function("SubscriptionManager")]
        public async Task Run([TimerTrigger("0 */30 * * * *")] TimerInfo timerInfo)
        {
            var graph = _graphFactory.Client;

            var mailbox       = _config["Dispatch__SharedMailbox"];
            var webhookUrl    = _config["Dispatch__WebhookUrl"];
            var lifecycleUrl  = _config["Dispatch__LifecycleWebhookUrl"]; // optional
            var useRich       = bool.TryParse(_config["Dispatch__UseRichNotifications"], out var b) && b;
            var encCertBase64 = _config["Dispatch__EncryptionCertBase64"];
            var encCertId     = _config["Dispatch__EncryptionCertId"] ?? "bgvfd-cert";

            if (string.IsNullOrWhiteSpace(mailbox) || string.IsNullOrWhiteSpace(webhookUrl))
            {
                _log.LogWarning("SubscriptionManager missing mailbox or webhookUrl.");
                await _audit.WriteAsync("subscription_config_missing", e => {});
                return;
            }

            var resourceBasic = $"/users/{mailbox}/mailFolders('inbox')/messages";
            var desiredLifetimeMinutes = useRich ? 1440 : 10080;

            var existingSubs = await graph.Subscriptions.GetAsync();
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
                if (!string.IsNullOrEmpty(lifecycleUrl))
                    sub.LifecycleNotificationUrl = lifecycleUrl;

                if (useRich && !string.IsNullOrWhiteSpace(encCertBase64))
                {
                    sub.IncludeResourceData = true;
                    sub.EncryptionCertificate = encCertBase64;
                    sub.EncryptionCertificateId = encCertId;
                    sub.Resource = $"/users/{mailbox}/mailFolders('inbox')/messages?$select=subject,from,receivedDateTime";
                }

                match = await graph.Subscriptions.PostAsync(sub);
                _log.LogInformation("Created Graph subscription {Id} exp {Exp}", match.Id, match.ExpirationDateTime);
                await _audit.WriteAsync("subscription_created", e => { e["subscriptionId"] = match.Id; e["expires"] = match.ExpirationDateTime; });
                return;
            }

            var threshold = TimeSpan.FromMinutes(useRich ? 30 : 120);
            if (match.ExpirationDateTime.HasValue && match.ExpirationDateTime.Value - DateTimeOffset.UtcNow < threshold)
            {
                var update = new Subscription
                {
                    ExpirationDateTime = DateTimeOffset.UtcNow.AddMinutes(desiredLifetimeMinutes)
                };
                if (useRich && !string.IsNullOrWhiteSpace(encCertBase64))
                {
                    update.IncludeResourceData = true;
                    update.EncryptionCertificate = encCertBase64;
                    update.EncryptionCertificateId = encCertId;
                    update.Resource = $"/users/{mailbox}/mailFolders('inbox')/messages?$select=subject,from,receivedDateTime";
                }
                await graph.Subscriptions[match.Id].PatchAsync(update);
                _log.LogInformation("Renewed Graph subscription {Id} new exp {Exp}", match.Id, update.ExpirationDateTime);
                await _audit.WriteAsync("subscription_renewed", e => { e["subscriptionId"] = match.Id; e["expires"] = update.ExpirationDateTime; });
            }
            else
            {
                await _audit.WriteAsync("subscription_ok", e => { e["subscriptionId"] = match.Id; e["expires"] = match.ExpirationDateTime; });
            }
        }

        private static string Normalize(string? resource)
        {
            if (string.IsNullOrWhiteSpace(resource)) return string.Empty;
            var idx = resource.IndexOf("/messages", StringComparison.OrdinalIgnoreCase);
            if (idx >= 0) return resource.Substring(0, idx + "/messages".Length);
            return resource.Trim();
        }
    }
}
