
// Notifications.cs — .NET 8 isolated-safe webhook handler for Microsoft Graph notifications
// No Microsoft.AspNetCore.WebUtilities or System.Web dependencies
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
            // Top-level guard to prevent 500 responses to Graph
            try
            {
                // -------- 0) Buffer request body ONCE and reuse as needed --------
                string requestBody = string.Empty;
                if (req.Body != null)
                {
                    using var ms = new MemoryStream();
                    await req.Body.CopyToAsync(ms);
                    ms.Position = 0;
                    using var sr = new StreamReader(ms);
                    requestBody = await sr.ReadToEndAsync();
                }

                // -------- 1) Validation handshake: echo validationToken and exit --------
                var queryDict = ParseQuery(req.Url.Query);
                string? validationToken = null;

                if (queryDict.TryGetValue("validationToken", out var tokenValues) && tokenValues.Count > 0)
                    validationToken = tokenValues[0];

                // Fallback: token in body (POST)
                if (string.IsNullOrWhiteSpace(validationToken) &&
                    string.Equals(req.Method, "POST", StringComparison.OrdinalIgnoreCase) &&
                    !string.IsNullOrWhiteSpace(requestBody))
                {
                    try
                    {
                        using var doc = JsonDocument.Parse(requestBody);
                        if (doc.RootElement.TryGetProperty("validationToken", out var tokEl) &&
                            tokEl.ValueKind == JsonValueKind.String)
                        {
                            validationToken = tokEl.GetString();
                        }
                    }
                    catch
                    {
                        // swallow parse errors during validation fallback
                    }
                }

                if (!string.IsNullOrWhiteSpace(validationToken))
                {
                    var res = req.CreateResponse(HttpStatusCode.OK);
                    res.Headers.Add("Content-Type", "text/plain; charset=utf-8");
                    await res.WriteStringAsync(validationToken);

                    // Audit in background; never block validation return
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

                // -------- 2) DI null guards --------
                if (_graphFactory is null || _iot is null || _audit is null || _config is null)
                {
                    var res = req.CreateResponse(HttpStatusCode.OK); // avoid 500 in webhook
                    await res.WriteStringAsync("{\"error\":\"di_unavailable\"}");
                    _log.LogError("DI service(s) null: GraphFactory={GraphNull}, IoT={IoTNull}, Audit={AuditNull}, Config={CfgNull}",
                        _graphFactory is null, _iot is null, _audit is null, _config is null);
                    return res;
                }

                // -------- 3) Normal notification processing --------
                var dispatchEnabledRaw = Get("Features:DispatchEnabled", "Features__DispatchEnabled");
                var dispatchEnabled = bool.TryParse(dispatchEnabledRaw, out var en) ? en : true;

                if (string.IsNullOrWhiteSpace(requestBody))
                {
                    _log.LogWarning("Empty notification body.");
                    var bad = req.CreateResponse(HttpStatusCode.BadRequest);
                    bad.Headers.Add("Content-Type", "application/json");
                    await bad.WriteStringAsync("{\"error\":\"empty_body\"}");
                    try { await _audit.WriteAsync("webhook_empty", e => { }); } catch { /* ignore */ }
                    return bad;
                }

                JsonDocument docRoot;
                try
                {
                    docRoot = JsonDocument.Parse(requestBody);
                }
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
                    var graph = _graphFactory.Client;

                    foreach (var n in notifications.EnumerateArray())
                    {
                        // Lifecycle notifications (e.g., "subscriptionRemoved")
                        if (n.TryGetProperty("lifecycleEvent", out var lifeEl) && lifeEl.ValueKind == JsonValueKind.String)
                        {
                            try { await _audit.WriteAsync("graph_lifecycle", e => { e["event"] = lifeEl.GetString(); }); } catch { }
                            continue;
                        }

                        string? userId = null;
                        string? messageId = null;

                        // Rich notifications: read resourceData
                        if (n.TryGetProperty("resourceData", out var rd) && rd.ValueKind == JsonValueKind.Object)
                        {
                            messageId = rd.TryGetProperty("id", out var idEl) ? idEl.GetString() : null;
                            userId = rd.TryGetProperty("userId", out var userEl) ? userEl.GetString() : null;
                        }

                        // Basic notifications: parse 'resource' if needed
                        if ((userId == null || messageId == null) && n.TryGetProperty("resource", out var resEl))
                        {
                            var resource = resEl.GetString();
                            if (string.IsNullOrEmpty(resource) || !resource.Contains("/messages/", StringComparison.OrdinalIgnoreCase))
                            {
                                try { await _audit.WriteAsync("skip_non_message", e => { e["resource"] = resource; }); } catch { }
                                continue;
                            }

                            // Matches /users/{userId}/messages/{messageId}
                            var m = Regex.Match(resource!, @"^/users/([^/]+)/messages/([^/?]+)", RegexOptions.IgnoreCase);
                            if (m.Success)
                            {
                                userId ??= Uri.UnescapeDataString(m.Groups[1].Value);
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

                            var subject = msg?.Subject ?? string.Empty;
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
                                        e["userId"] = userId;
                                        e["messageId"] = messageId;
                                        e["subject"] = subject;
                                        e["deviceId"] = deviceId;
                                        e["via"] = result.via;
                                        e["status"] = result.status;
                                    });
                                }
                                catch { }

                                // Mark read and optionally move to "Processed"
                                await graph.Users[userId].Messages[messageId].PatchAsync(new Message { IsRead = true });

                                var folders = await graph.Users[userId].MailFolders.GetAsync();
                                var processedFolder = folders?.Value?.FirstOrDefault(f =>
                                    f.DisplayName != null && f.DisplayName.Equals("Processed", StringComparison.OrdinalIgnoreCase));

                                if (processedFolder != null)
                                {
                                    await graph.Users[userId].Messages[messageId].Move.PostAsync(
                                        new Microsoft.Graph.Users.Item.Messages.Item.Move.MovePostRequestBody
                                        {
                                            DestinationId = processedFolder.Id
                                        });

                                    try
                                    {
                                        await _audit.WriteAsync("message_moved", e =>
                                        {
                                            e["messageId"] = messageId;
                                            e["folderId"] = processedFolder.Id;
                                        });
                                    }
                                    catch { }
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
                            // Per-notification error; keep loop going
                            try
                            {
                                await _audit.WriteAsync("dispatch_error", e =>
                                {
                                    e["userId"] = userId;
                                    e["messageId"] = messageId;
                                    e["error"] = ex.Message;
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
            catch (Exception ex)
            {
                // Ensure webhook never returns 500; surface lightweight diagnostics
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
                if (!string.IsNullOrWhiteSpace(kvp.Key) &&
                    subject.IndexOf(kvp.Key, StringComparison.OrdinalIgnoreCase) >= 0)
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
                var device = child.Value ?? string.Empty;
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

        // ---- Minimal query string parser (isolated friendly, no external deps) ----
        // Returns dictionary of key -> list of values (to mirror typical query parsing semantics)
        private static Dictionary<string, List<string>> ParseQuery(string queryString)
        {
            var result = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);

            if (string.IsNullOrEmpty(queryString))
                return result;

            // Strip leading '?'
            var q = queryString[0] == '?' ? queryString.Substring(1) : queryString;

            foreach (var pair in q.Split('&', StringSplitOptions.RemoveEmptyEntries))
            {
                var idx = pair.IndexOf('=');
                string rawKey, rawVal;
                if (idx >= 0)
                {
                    rawKey = pair.Substring(0, idx);
                    rawVal = pair.Substring(idx + 1);
                }
                else
                {
                    rawKey = pair;
                    rawVal = string.Empty;
                }

                var key = Uri.UnescapeDataString(rawKey.Replace('+', ' '));
                var val = Uri.UnescapeDataString(rawVal.Replace('+', ' '));

                if (!result.TryGetValue(key, out var list))
                {
                    list = new List<string>();
                    result[key] = list;
                }
                list.Add(val);
            }

            return result;
        }
    }
}
