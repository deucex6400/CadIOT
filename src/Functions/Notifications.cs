using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Azure.Functions.Worker.Http;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Configuration;
using cad_dispatch.Services;
using Microsoft.Graph.Models;

namespace cad_dispatch.Functions
{
    public class Notifications
    {
        private readonly GraphClientFactory _graphFactory;
        private readonly IoTHubService _iot;
        private readonly IConfiguration _config;
        private readonly AuditLogService _audit;
        private readonly ILogger<Notifications> _log;

        public Notifications(
            GraphClientFactory graphFactory,
            IoTHubService iot,
            IConfiguration config,
            AuditLogService audit,
            ILogger<Notifications> log)
        {
            _graphFactory = graphFactory;
            _iot = iot;
            _config = config;
            _audit = audit;
            _log = log;
        }

        [Function("Notifications")]
        public async Task<HttpResponseData> Run(
            [HttpTrigger(AuthorizationLevel.Anonymous, "get", "post")] HttpRequestData req)
        {
            // Validation handshake
            var query = System.Web.HttpUtility.ParseQueryString(req.Url.Query);
            var validationToken = query.Get("validationToken");
            if (!string.IsNullOrEmpty(validationToken))
            {
                var res = req.CreateResponse(System.Net.HttpStatusCode.OK);
                res.Headers.Add("Content-Type", "text/plain");
                await res.WriteStringAsync(validationToken);
                _log.LogInformation("Validation completed.");
                await _audit.WriteAsync("webhook_validation", e => { e["tokenLen"] = validationToken.Length; });
                return res;
            }

            // Optional feature flag to disable dispatch
            var dispatchEnabled = bool.TryParse(_config["Features__DispatchEnabled"], out var en) ? en : true;

            var body = await new StreamReader(req.Body).ReadToEndAsync();
            if (string.IsNullOrWhiteSpace(body))
            {
                _log.LogWarning("Empty notification body.");
                var bad = req.CreateResponse(System.Net.HttpStatusCode.BadRequest);
                await bad.WriteStringAsync("{\"error\":\"empty_body\"}");
                await _audit.WriteAsync("webhook_empty", e => {});
                return bad;
            }

            JsonDocument doc;
            try { doc = JsonDocument.Parse(body); }
            catch (Exception ex)
            {
                _log.LogError(ex, "Failed to parse notification JSON.");
                var bad = req.CreateResponse(System.Net.HttpStatusCode.BadRequest);
                await bad.WriteStringAsync("{\"error\":\"invalid_json\"}");
                await _audit.WriteAsync("webhook_invalid_json", e => { e["error"] = ex.Message; });
                return bad;
            }

            if (doc.RootElement.TryGetProperty("value", out var notifications) &&
                notifications.ValueKind == JsonValueKind.Array)
            {
                var graph = _graphFactory.Client;

                foreach (var n in notifications.EnumerateArray())
                {
                    if (n.TryGetProperty("lifecycleEvent", out var lifeEl) && lifeEl.ValueKind == JsonValueKind.String)
                    {
                        await _audit.WriteAsync("graph_lifecycle", e => { e["event"] = lifeEl.GetString(); });
                        continue;
                    }

                    string? userId = null;
                    string? messageId = null;

                    if (n.TryGetProperty("resourceData", out var rd) && rd.ValueKind == JsonValueKind.Object)
                    {
                        messageId = rd.TryGetProperty("id", out var idEl) ? idEl.GetString() : null;
                        userId = rd.TryGetProperty("userId", out var userEl) ? userEl.GetString() : null;
                    }

                    if ((userId == null || messageId == null) && n.TryGetProperty("resource", out var resEl))
                    {
                        var resource = resEl.GetString();
                        if (string.IsNullOrEmpty(resource) || !resource.Contains("/messages/"))
                        {
                            await _audit.WriteAsync("skip_non_message", e => { e["resource"] = resource; });
                            continue;
                        }
                        var m = Regex.Match(resource, @"^/users/([^/]+)/messages/([^/?]+)", RegexOptions.IgnoreCase);
                        if (m.Success)
                        {
                            userId ??= Uri.UnescapeDataString(m.Groups[1].Value);
                            messageId ??= Uri.UnescapeDataString(m.Groups[2].Value);
                        }
                        else
                        {
                            await _audit.WriteAsync("parse_resource_failed", e => { e["resource"] = resource; });
                            continue;
                        }
                    }

                    if (string.IsNullOrEmpty(userId) || string.IsNullOrEmpty(messageId))
                    {
                        await _audit.WriteAsync("missing_ids", e => {});
                        continue;
                    }

                    try
                    {
                        var msg = await graph.Users[userId].Messages[messageId].GetAsync(r =>
                        {
                            r.QueryParameters.Select = new[] { "subject" };
                        });
                        var subject = msg?.Subject ?? string.Empty;

                        var deviceId = ResolveDeviceIdFromConfig(subject);
                        if (!dispatchEnabled)
                        {
                            _log.LogWarning("Dispatch disabled via feature flag.");
                            await _audit.WriteAsync("dispatch_disabled", e => { e["subject"] = subject; });
                            continue;
                        }

                        if (!string.IsNullOrEmpty(deviceId))
                        {
                            var result = await _iot.TriggerRelayAsync(deviceId, new { subject, reason = "CAD" });
                            await _audit.WriteAsync("dispatch_triggered", e => {
                                e["userId"] = userId; e["messageId"] = messageId;
                                e["subject"] = subject; e["deviceId"] = deviceId;
                                e["via"] = result.via; e["status"] = result.status;
                            });

                            await graph.Users[userId].Messages[messageId].PatchAsync(new Message { IsRead = true });
                            var folders = await graph.Users[userId].MailFolders.GetAsync();
                            var processedFolder = folders.Value.FirstOrDefault(f => f.DisplayName.Equals("Processed", StringComparison.OrdinalIgnoreCase));
                            if (processedFolder != null)
                            {
                                await graph.Users[userId].Messages[messageId].Move.PostAsync(
                                    new Microsoft.Graph.Users.Item.Messages.Item.Move.MovePostRequestBody
                                    { DestinationId = processedFolder.Id });
                                await _audit.WriteAsync("message_moved", e => { e["messageId"] = messageId; e["folderId"] = processedFolder.Id; });
                            }
                            else
                            {
                                await _audit.WriteAsync("processed_folder_missing", e => {});
                            }
                        }
                        else
                        {
                            await _audit.WriteAsync("no_device_mapping", e => { e["subject"] = subject; });
                        }
                    }
                    catch (Exception ex)
                    {
                        await _audit.WriteAsync("dispatch_error", e => { e["userId"] = userId; e["messageId"] = messageId; e["error"] = ex.Message; });
                    }
                }
            }

            var ok = req.CreateResponse(System.Net.HttpStatusCode.OK);
            ok.Headers.Add("Content-Type", "application/json");
            await ok.WriteStringAsync("{\"ok\":true}");
            return ok;
        }

        private string? ResolveDeviceIdFromConfig(string subject)
        {
            var routes = GetDispatchRoutes();
            foreach (var kvp in routes)
            {
                if (!string.IsNullOrWhiteSpace(kvp.Key) && subject.IndexOf(kvp.Key, StringComparison.OrdinalIgnoreCase) >= 0)
                    return kvp.Value;
            }
            // fallback
            if (subject.IndexOf("DISPATCH-1", StringComparison.OrdinalIgnoreCase) >= 0) return "Relay-1";
            if (subject.IndexOf("DISPATCH-2", StringComparison.OrdinalIgnoreCase) >= 0) return "Relay-2";
            if (subject.IndexOf("DISPATCH-3", StringComparison.OrdinalIgnoreCase) >= 0) return "Relay-3";
            return null;
        }

        private IReadOnlyDictionary<string, string> GetDispatchRoutes()
        {
            var section = _config.GetSection("Dispatch:Routes");
            var dict = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var child in section.GetChildren())
            {
                var pattern = child.Key; var device = child.Value ?? string.Empty;
                if (!string.IsNullOrWhiteSpace(pattern) && !string.IsNullOrWhiteSpace(device))
                    dict[pattern] = device;
            }
            return dict;
        }
    }
}
