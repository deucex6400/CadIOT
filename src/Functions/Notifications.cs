// Patched Notifications.cs — early-return validation to avoid 500 during Graph subscription creation
// Joe Doucet / CadIOT
using System;
using System.IO;
using System.Linq;
using System.Collections.Generic;
using System.Net;
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

        // Helper: prefer App Configuration colon keys, fall back to env var double-underscore
        private string? Get(string colonKey, string underscoreKey) => _config[colonKey] ?? _config[underscoreKey];

        [Function("Notifications")]
        public async Task<HttpResponseData> Run(
            [HttpTrigger(AuthorizationLevel.Anonymous, "get", "post")] HttpRequestData req)
        {
            // 1) Validation handshake (Graph webhook): echo validationToken and return 200 ASAP
            var query = System.Web.HttpUtility.ParseQueryString(req.Url.Query);
            string? validationToken = query.Get("validationToken");

            // Some clients send validation via POST with token in body; try lightweight fallback
            if (string.IsNullOrWhiteSpace(validationToken) &&
                string.Equals(req.Method, "POST", StringComparison.OrdinalIgnoreCase))
            {
                try
                {
                    using var reader = new StreamReader(req.Body);
                    var body = await reader.ReadToEndAsync();
                    using var doc = JsonDocument.Parse(body);
                    if (doc.RootElement.TryGetProperty("validationToken", out var tokEl) && tokEl.ValueKind == JsonValueKind.String)
                        validationToken = tokEl.GetString();
                }
                catch
                {
                    // swallow parse errors during validation fallback
                }
            }

            if (!string.IsNullOrWhiteSpace(validationToken))
            {
                var res = req.CreateResponse(HttpStatusCode.OK);
                res.Headers.Add("Content-Type", "text/plain");
                await res.WriteStringAsync(validationToken);

                // Audit in background; never block or fail validation
                _ = Task.Run(async () =>
                {
                    try
                    {
                        _log.LogInformation("Graph validation received; token length={Len}", validationToken!.Length);
                        await _audit.WriteAsync("webhook_validation", e =>
                        {
                            e["tokenLen"] = validationToken.Length;
                            e["when"] = DateTime.UtcNow;
                        });
                    }
                    catch (Exception ex)
                    {
                        _log.LogWarning(ex, "Audit logging failed during validation; ignoring.");
                    }
                });

                return res; // IMPORTANT: return immediately to guarantee 200 OK to Graph
            }

            // 2) Normal notification processing
            var dispatchEnabledRaw = Get("Features:DispatchEnabled", "Features__DispatchEnabled");
            var dispatchEnabled = bool.TryParse(dispatchEnabledRaw, out var en) ? en : true;

            string requestBody;
            using (var sr = new StreamReader(req.Body))
                requestBody = await sr.ReadToEndAsync();

            if (string.IsNullOrWhiteSpace(requestBody))
            {
                _log.LogWarning("Empty notification body.");
                var bad = req.CreateResponse(HttpStatusCode.BadRequest);
                await bad.WriteStringAsync("{\"error\":\"empty_body\"}");
                try { await _audit.WriteAsync("webhook_empty", e => { }); } catch { /* ignore */ }
                return bad;
            }

            JsonDocument docRoot;
            try { docRoot = JsonDocument.Parse(requestBody); }
            catch (Exception ex)
            {
                _log.LogError(ex, "Failed to parse notification JSON.");
                var bad = req.CreateResponse(HttpStatusCode.BadRequest);
                await bad.WriteStringAsync("{\"error\":\"invalid_json\"}");
                try { await _audit.WriteAsync("webhook_invalid_json", e => { e["error"] = ex.Message; }); } catch { }
                return bad;
            }

            if (docRoot.RootElement.TryGetProperty("value", out var notifications) &&
                notifications.ValueKind == JsonValueKind.Array)
            {
                var graph = _graphFactory.Client;

                foreach (var n in notifications.EnumerateArray())
                {
                    // Lifecycle notifications
                    if (n.TryGetProperty("lifecycleEvent", out var lifeEl) && lifeEl.ValueKind == JsonValueKind.String)
                    {
                        try { await _audit.WriteAsync("graph_lifecycle", e => { e["event"] = lifeEl.GetString(); }); } catch { }
                        continue;
                    }

                    string? userId = null;
                    string? messageId = null;

                    // Rich notifications
                    if (n.TryGetProperty("resourceData", out var rd) && rd.ValueKind == JsonValueKind.Object)
                    {
                        messageId = rd.TryGetProperty("id", out var idEl) ? idEl.GetString() : null;
                        userId    = rd.TryGetProperty("userId", out var userEl) ? userEl.GetString() : null;
                    }

                    // Basic notifications: parse 'resource'
                    if ((userId == null || messageId == null) && n.TryGetProperty("resource", out var resEl))
                    {
                        var resource = resEl.GetString();
                        if (string.IsNullOrEmpty(resource) || !resource.Contains("/messages/", StringComparison.OrdinalIgnoreCase))
                        {
                            try { await _audit.WriteAsync("skip_non_message", e => { e["resource"] = resource; }); } catch { }
                            continue;
                        }

                        // Matches /users/{userId}/messages/{messageId} and similar
                        var m = Regex.Match(resource!, @"^/users/([^/]+)/messages/([^/?]+)", RegexOptions.IgnoreCase);
                        if (m.Success)
                        {
                            userId    ??= Uri.UnescapeDataString(m.Groups[1].Value);
                            messageId ??= Uri.UnescapeDataString(m.Groups[2].Value);
                        }
                        else
                        {
                            try { await _audit.WriteAsync("parse_resource_failed", e => { e["resource"] = resource; }); } catch { }
                            continue;
                        }
                    }

                    if (string.IsNullOrEmpty(userId) || string.IsNullOrEmpty(messageId))
                    {
                        try { await _audit.WriteAsync("missing_ids", e => { }); } catch { }
                        continue;
                    }

                    try
                    {
                        var msg = await graph.Users[userId].Messages[messageId].GetAsync(r =>
                        {
                            r.QueryParameters.Select = new[] { "subject" };
                        });
                        var subject  = msg?.Subject ?? string.Empty;
                        var deviceId = ResolveDeviceIdFromConfig(subject);

                        if (!dispatchEnabled)
                        {
                            _log.LogWarning("Dispatch disabled via feature flag.");
                            try { await _audit.WriteAsync("dispatch_disabled", e => { e["subject"] = subject; }); } catch { }
                            continue;
                        }

                        if (!string.IsNullOrEmpty(deviceId))
                        {
                            var result = await _iot.TriggerRelayAsync(deviceId, new { subject, reason = "CAD" });
                            try
                            {
                                await _audit.WriteAsync("dispatch_triggered", e =>
                                {
                                    e["userId"]   = userId;
                                    e["messageId"] = messageId;
                                    e["subject"]  = subject;
                                    e["deviceId"] = deviceId;
                                    e["via"]      = result.via;
                                    e["status"]   = result.status;
                                });
                            }
                            catch { }

                            // Mark read and optionally move to "Processed"
                            await graph.Users[userId].Messages[messageId].PatchAsync(new Message { IsRead = true });
                            var folders = await graph.Users[userId].MailFolders.GetAsync();
                            var processedFolder = folders?.Value?.FirstOrDefault(f => f.DisplayName != null && f.DisplayName.Equals("Processed", StringComparison.OrdinalIgnoreCase));

                            if (processedFolder != null)
                            {
                                await graph.Users[userId].Messages[messageId].Move.PostAsync(
                                    new Microsoft.Graph.Users.Item.Messages.Item.Move.MovePostRequestBody
                                    {
                                        DestinationId = processedFolder.Id
                                    });
                                try { await _audit.WriteAsync("message_moved", e => { e["messageId"] = messageId; e["folderId"] = processedFolder.Id; }); } catch { }
                            }
                            else
                            {
                                try { await _audit.WriteAsync("processed_folder_missing", e => { }); } catch { }
                            }
                        }
                        else
                        {
                            try { await _audit.WriteAsync("no_device_mapping", e => { e["subject"] = subject; }); } catch { }
                        }
                    }
                    catch (Exception ex)
                    {
                        try
                        {
                            await _audit.WriteAsync("dispatch_error", e =>
                            {
                                e["userId"]    = userId;
                                e["messageId"] = messageId;
                                e["error"]     = ex.Message;
                            });
                        }
                        catch { }
                    }
                }
            }

            var ok = req.CreateResponse(HttpStatusCode.OK);
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
            // Simple fallbacks
            if (subject.IndexOf("DISPATCH-1", StringComparison.OrdinalIgnoreCase) >= 0) return "Relay-1";
            if (subject.IndexOf("DISPATCH-2", StringComparison.OrdinalIgnoreCase) >= 0) return "Relay-2";
            if (subject.IndexOf("DISPATCH-3", StringComparison.OrdinalIgnoreCase) >= 0) return "Relay-3";
            return null;
        }

        private IReadOnlyDictionary<string, string> GetDispatchRoutes()
        {
            // Prefer App Config colon section
            var section = _config.GetSection("Dispatch:Routes");
            var dict = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var child in section.GetChildren())
            {
                var pattern = child.Key;
                var device  = child.Value ?? string.Empty;
                if (!string.IsNullOrWhiteSpace(pattern) && !string.IsNullOrWhiteSpace(device))
                    dict[pattern] = device;
            }

            // Fallback to a few known env keys
            if (dict.Count == 0)
            {
                var r1 = Get("Dispatch:Routes:DISPATCH-1", "Dispatch__Routes__DISPATCH-1");
                var r2 = Get("Dispatch:Routes:DISPATCH-2", "Dispatch__Routes__DISPATCH-2");
                var r3 = Get("Dispatch:Routes:DISPATCH-3", "Dispatch__Routes__DISPATCH-3");
                if (!string.IsNullOrWhiteSpace(r1)) dict["DISPATCH-1"] = r1!;
                if (!string.IsNullOrWhiteSpace(r2)) dict["DISPATCH-2"] = r2!;
                if (!string.IsNullOrWhiteSpace(r3)) dict["DISPATCH-3"] = r3!;
            }
            return dict;
        }
    }
}
