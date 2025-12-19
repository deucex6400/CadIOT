// Notifications.cs — .NET 8 isolated-safe webhook handler for Microsoft Graph notifications
// Diagnostics-enhanced version
// Joe Doucet / CadIOT
using System;
using System.IO;
using System.Linq;
using System.Collections.Generic;
using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
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
            try
            {
                // 0) Buffer request body ONCE
                string requestBody = string.Empty;
                if (req.Body != null)
                {
                    using var ms = new MemoryStream();
                    await req.Body.CopyToAsync(ms);
                    ms.Position = 0;
                    using var sr = new StreamReader(ms);
                    requestBody = await sr.ReadToEndAsync();
                }

                // 1) Validation handshake
                var queryDict = ParseQuery(req.Url.Query);
                string? validationToken = null;
                if (queryDict.TryGetValue("validationToken", out var tokenValues) && tokenValues.Count > 0)
                    validationToken = tokenValues[0];

                if (string.IsNullOrWhiteSpace(validationToken) &&
                    string.Equals(req.Method, "POST", StringComparison.OrdinalIgnoreCase) &&
                    !string.IsNullOrWhiteSpace(requestBody))
                {
                    try
                    {
                        using var doc = JsonDocument.Parse(requestBody);
                        if (doc.RootElement.TryGetProperty("validationToken", out var tokEl) && tokEl.ValueKind == JsonValueKind.String)
                            validationToken = tokEl.GetString();
                    }
                    catch { }
                }

                if (!string.IsNullOrWhiteSpace(validationToken))
                {
                    var res = req.CreateResponse(HttpStatusCode.OK);
                    res.Headers.Add("Content-Type", "text/plain; charset=utf-8");
                    await res.WriteStringAsync(validationToken);
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
                    return res;
                }

                // 2) DI null guards
                if (_graphFactory is null || _iot is null || _audit is null || _config is null)
                {
                    var res = req.CreateResponse(HttpStatusCode.OK);
                    await res.WriteStringAsync("{\"error\":\"di_unavailable\"}");
                    _log.LogError("DI service(s) null: GraphFactory={GraphNull}, IoT={IoTNull}, Audit={AuditNull}, Config={CfgNull}",
                        _graphFactory is null, _iot is null, _audit is null, _config is null);
                    return res;
                }

                // 3) Normal processing with diagnostics
                var dispatchEnabledRaw = Get("Features:DispatchEnabled", "Features__DispatchEnabled");
                var dispatchEnabled = bool.TryParse(dispatchEnabledRaw, out var en) ? en : true;

                if (string.IsNullOrWhiteSpace(requestBody))
                {
                    _log.LogWarning("Empty notification body.");
                    var bad = req.CreateResponse(HttpStatusCode.BadRequest);
                    bad.Headers.Add("Content-Type", "application/json");
                    await bad.WriteStringAsync("{\"error\":\"empty_body\"}");
                    try { await _audit.WriteAsync("webhook_empty", e => { }); } catch { }
                    return bad;
                }

                JsonDocument docRoot;
                try { docRoot = JsonDocument.Parse(requestBody); }
                catch (Exception ex)
                {
                    _log.LogError(ex, "Failed to parse notification JSON.");
                    var bad = req.CreateResponse(HttpStatusCode.BadRequest);
                    bad.Headers.Add("Content-Type", "application/json");
                    await bad.WriteStringAsync("{\"error\":\"invalid_json\"}");
                    try { await _audit.WriteAsync("webhook_invalid_json", e => { e["error"] = ex.Message; }); } catch { }
                    return bad;
                }

                if (docRoot.RootElement.TryGetProperty("value", out var notifications) &&
                    notifications.ValueKind == JsonValueKind.Array)
                {
                    int total = notifications.GetArrayLength();
                    _log.LogInformation("Webhook received {Count} notification(s)", total);
                    try { await _audit.WriteAsync("webhook_received", e => { e["count"] = total; }); } catch { }

                    var graph = _graphFactory.Client;

                    foreach (var n in notifications.EnumerateArray())
                    {
                        // Lifecycle notifications
                        if (n.TryGetProperty("lifecycleEvent", out var lifeEl) && lifeEl.ValueKind == JsonValueKind.String)
                        {
                            var ev = lifeEl.GetString();
                            _log.LogInformation("Lifecycle event received: {Event}", ev);
                            try { await _audit.WriteAsync("graph_lifecycle", e => { e["event"] = ev; }); } catch { }
                            continue;
                        }

                        string? userId = null;
                        string? messageId = null;
                        string? resource = null;

                        // Rich notifications
                        if (n.TryGetProperty("resourceData", out var rd) && rd.ValueKind == JsonValueKind.Object)
                        {
                            messageId = rd.TryGetProperty("id", out var idEl) ? idEl.GetString() : null;
                            userId = rd.TryGetProperty("userId", out var userEl) ? userEl.GetString() : null;
                            _log.LogInformation("resourceData ids: userId={UserId}, messageId={MessageId}", userId, messageId);
                        }

                        // Basic notifications
                        if ((userId == null || messageId == null) && n.TryGetProperty("resource", out var resEl))
                        {
                            resource = resEl.GetString();
                            _log.LogInformation("Notification resource='{Resource}'", resource);

                            if (string.IsNullOrEmpty(resource) || !resource.Contains("/messages/", StringComparison.OrdinalIgnoreCase))
                            {
                                _log.LogInformation("Skipping non-message notification; resource='{Resource}'", resource);
                                try { await _audit.WriteAsync("skip_non_message", e => { e["resource"] = resource; }); } catch { }
                                continue;
                            }

                            var m = Regex.Match(resource!, @"^/users/([^/]+)/messages/([^/?]+)", RegexOptions.IgnoreCase);
                            if (m.Success)
                            {
                                userId ??= Uri.UnescapeDataString(m.Groups[1].Value);
                                messageId ??= Uri.UnescapeDataString(m.Groups[2].Value);
                            }
                            else
                            {
                                _log.LogWarning("Failed to parse resource='{Resource}'", resource);
                                try { await _audit.WriteAsync("parse_resource_failed", e => { e["resource"] = resource; }); } catch { }
                                continue;
                            }
                        }

                        if (string.IsNullOrEmpty(userId) || string.IsNullOrEmpty(messageId))
                        {
                            _log.LogWarning("Missing ids; userId='{UserId}', messageId='{MessageId}'", userId, messageId);
                            try { await _audit.WriteAsync("missing_ids", e => { }); } catch { }
                            continue;
                        }

                        try
                        {
                            var msg = await graph.Users[userId].Messages[messageId].GetAsync(r =>
                            {
                                r.QueryParameters.Select = new[] { "subject" };
                            });
                            var subject = msg?.Subject ?? string.Empty;
                            _log.LogInformation("Processing messageId={MessageId} userId={UserId} subject='{Subject}'", messageId, userId, subject);

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
                                _log.LogInformation("Dispatch triggered to deviceId={DeviceId} via={Via} status={Status}", deviceId, result.via, result.status);
                                try
                                {
                                    await _audit.WriteAsync("dispatch_triggered", e =>
                                    {
                                        e["userId"] = userId;
                                        e["messageId"] = messageId;
                                        e["subject"] = subject;
                                        e["deviceId"] = deviceId;
                                        e["via"] = result.via;
                                        e["status"] = result.status;
                                    });
                                }
                                catch { }

                                // Mark read and move to Processed
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
                                    _log.LogInformation("Message moved to folderId={FolderId}", processedFolder.Id);
                                    try { await _audit.WriteAsync("message_moved", e => { e["messageId"] = messageId; e["folderId"] = processedFolder.Id; }); } catch { }
                                }
                                else
                                {
                                    _log.LogWarning("Processed folder missing");
                                    try { await _audit.WriteAsync("processed_folder_missing", e => { }); } catch { }
                                }
                            }
                            else
                            {
                                _log.LogInformation("No device mapping for subject='{Subject}'", subject);
                                try { await _audit.WriteAsync("no_device_mapping", e => { e["subject"] = subject; }); } catch { }
                            }
                        }
                        catch (Exception ex)
                        {
                            _log.LogError(ex, "Error processing messageId={MessageId} userId={UserId}", messageId, userId);
                            try { await _audit.WriteAsync("dispatch_error", e => { e["userId"] = userId; e["messageId"] = messageId; e["error"] = ex.Message; }); } catch { }
                        }
                    }
                }

                var ok = req.CreateResponse(HttpStatusCode.OK);
                ok.Headers.Add("Content-Type", "application/json");
                await ok.WriteStringAsync("{\"ok\":true}");
                return ok;
            }
            catch (Exception ex)
            {
                _log.LogError(ex, "Unhandled exception in Notifications.Run");
                var res = req.CreateResponse(HttpStatusCode.OK);
                res.Headers.Add("Content-Type", "application/json");
                await res.WriteStringAsync("{\"error\":\"unhandled\",\"detail\":\"see logs\"}");
                return res;
            }
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
            var section = _config.GetSection("Dispatch:Routes");
            var dict = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var child in section.GetChildren())
            {
                var pattern = child.Key;
                var device = child.Value ?? string.Empty;
                if (!string.IsNullOrWhiteSpace(pattern) && !string.IsNullOrWhiteSpace(device))
                    dict[pattern] = device;
            }
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

        private static Dictionary<string, List<string>> ParseQuery(string queryString)
        {
            var result = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
            if (string.IsNullOrEmpty(queryString)) return result;
            var q = queryString[0] == '?' ? queryString.Substring(1) : queryString;
            foreach (var pair in q.Split('&', StringSplitOptions.RemoveEmptyEntries))
            {
                var idx = pair.IndexOf('=');
                string rawKey, rawVal;
                if (idx >= 0) { rawKey = pair.Substring(0, idx); rawVal = pair.Substring(idx + 1); }
                else { rawKey = pair; rawVal = string.Empty; }
                var key = Uri.UnescapeDataString(rawKey.Replace('+', ' '));
                var val = Uri.UnescapeDataString(rawVal.Replace('+', ' '));
                if (!result.TryGetValue(key, out var list)) { list = new List<string>(); result[key] = list; }
                list.Add(val);
            }
            return result;
        }
    }
}