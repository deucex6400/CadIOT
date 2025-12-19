// Notifications.cs — patched v3 (supports segment- and function-style message resources)
using cad_dispatch.Services;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Azure.Functions.Worker.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Graph.Models;
using System.Net;
using System.Text.Json;
using System.Text.RegularExpressions;

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

        private string? Get(string colonKey, string underscoreKey) => _config[colonKey] ?? _config[underscoreKey];

        [Function("Notifications")]
        public async Task<HttpResponseData> Run(
            [HttpTrigger(AuthorizationLevel.Anonymous, "get", "post")] HttpRequestData req)
        {
            try
            {
                // Buffer the request body once
                string requestBody = string.Empty;
                if (req.Body != null)
                {
                    using var ms = new MemoryStream();
                    await req.Body.CopyToAsync(ms);
                    ms.Position = 0;
                    using var sr = new StreamReader(ms);
                    requestBody = await sr.ReadToEndAsync();
                }

                // Validation handshake — must echo validationToken as text/plain within ~10s
                var query = ParseQuery(req.Url.Query);
                if (query.TryGetValue("validationToken", out var vals) && vals.Count > 0)
                {
                    var res = req.CreateResponse(HttpStatusCode.OK);
                    res.Headers.Add("Content-Type", "text/plain; charset=utf-8");
                    await res.WriteStringAsync(vals[0]);
                    _ = Task.Run(async () =>
                    {
                        try { await _audit.WriteAsync("webhook_validation", e => { e["tokenLen"] = vals[0].Length; e["when"] = DateTime.UtcNow; }); } catch { }
                    });
                    return res; // return immediately
                }

                // Guard DI
                if (_graphFactory is null || _iot is null || _audit is null || _config is null)
                {
                    var res = req.CreateResponse(HttpStatusCode.OK);
                    await res.WriteStringAsync("{\"error\":\"di_unavailable\"}");
                    _log.LogError("DI service(s) null: GraphFactory={GraphNull}, IoT={IoTNull}, Audit={AuditNull}, Config={CfgNull}",
                        _graphFactory is null, _iot is null, _audit is null, _config is null);
                    return res;
                }

                // Feature flag
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

                JsonDocument root;
                try { root = JsonDocument.Parse(requestBody); }
                catch (Exception ex)
                {
                    _log.LogError(ex, "Failed to parse notification JSON.");
                    var bad = req.CreateResponse(HttpStatusCode.BadRequest);
                    bad.Headers.Add("Content-Type", "application/json");
                    await bad.WriteStringAsync("{\"error\":\"invalid_json\"}");
                    try { await _audit.WriteAsync("webhook_invalid_json", e => { e["error"] = ex.Message; }); } catch { }
                    return bad;
                }

                string sharedMailbox = Get("Dispatch:SharedMailbox", "Dispatch__SharedMailbox") ?? string.Empty;

                if (root.RootElement.TryGetProperty("value", out var notifications) && notifications.ValueKind == JsonValueKind.Array)
                {
                    var graph = _graphFactory.Client;
                    foreach (var n in notifications.EnumerateArray())
                    {
                        // lifecycle notifications
                        if (n.TryGetProperty("lifecycleEvent", out var lifeEl) && lifeEl.ValueKind == JsonValueKind.String)
                        {
                            try { await _audit.WriteAsync("graph_lifecycle", e => { e["event"] = lifeEl.GetString(); }); } catch { }
                            continue;
                        }

                        string? userId = null;
                        string? messageId = null;

                        // rich notifications
                        if (n.TryGetProperty("resourceData", out var rd) && rd.ValueKind == JsonValueKind.Object)
                        {
                            messageId = rd.TryGetProperty("id", out var idEl) ? idEl.GetString() : null;
                            userId = rd.TryGetProperty("userId", out var userEl) ? userEl.GetString() : null;
                        }

                        // basic notifications — parse resource
                        if ((userId == null || messageId == null) && n.TryGetProperty("resource", out var resEl))
                        {
                            var resource = resEl.GetString();
                            if (string.IsNullOrEmpty(resource) || resource.IndexOf("/messages", StringComparison.OrdinalIgnoreCase) < 0)
                            {
                                try { await _audit.WriteAsync("skip_non_message", e => { e["resource"] = resource; }); } catch { }
                                _log.LogWarning("Skipping resource that does not include '/messages': {Resource}", resource);
                                continue;
                            }

                            // Try segment-style first: /users/{userId}/.../messages/{messageId}
                            var seg = Regex.Match(resource!, @"^/?users/([^/]+)(?:/.*)?/messages/([^/?]+)", RegexOptions.IgnoreCase);

                            // Then function-style: Users('<userId>')/messages('<messageId>') or users('...')
                            var fun = Regex.Match(resource!, @"^/?users\('([^']+)'\)/messages\('([^']+)'\)", RegexOptions.IgnoreCase);
                            if (!fun.Success)
                                fun = Regex.Match(resource!, @"^/?Users\('([^']+)'\)/messages\('([^']+)'\)", RegexOptions.IgnoreCase);

                            if (seg.Success)
                            {
                                userId ??= Uri.UnescapeDataString(seg.Groups[1].Value);
                                messageId ??= Uri.UnescapeDataString(seg.Groups[2].Value);
                                try { await _audit.WriteAsync("resource_parsed", e => { e["style"] = "segment"; e["resource"] = resource; }); } catch { }
                            }
                            else if (fun.Success)
                            {
                                userId ??= Uri.UnescapeDataString(fun.Groups[1].Value);
                                messageId ??= Uri.UnescapeDataString(fun.Groups[2].Value);
                                try { await _audit.WriteAsync("resource_parsed", e => { e["style"] = "function"; e["resource"] = resource; }); } catch { }
                            }
                            else
                            {
                                try { await _audit.WriteAsync("parse_resource_failed", e => { e["resource"] = resource; }); } catch { }
                                _log.LogWarning("Failed to parse message resource: {Resource}", resource);
                                continue;
                            }
                        }

                        // fallback to configured mailbox
                        if (string.IsNullOrEmpty(userId) && !string.IsNullOrWhiteSpace(sharedMailbox))
                            userId = sharedMailbox;

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
                                        e["userId"] = userId; e["messageId"] = messageId; e["subject"] = subject; e["deviceId"] = deviceId; e["via"] = result.via; e["status"] = result.status;
                                    });
                                }
                                catch { }

                                // mark as read
                                await graph.Users[userId].Messages[messageId].PatchAsync(new Message { IsRead = true });

                                // move to Processed (create if missing)
                                var folders = await graph.Users[userId].MailFolders.GetAsync();
                                var processedFolder = folders?.Value?.FirstOrDefault(f => f.DisplayName != null && f.DisplayName.Equals("Processed", StringComparison.OrdinalIgnoreCase));
                                if (processedFolder == null)
                                {
                                    try
                                    {
                                        var inbox = folders?.Value?.FirstOrDefault(f => f.DisplayName != null && f.DisplayName.Equals("Inbox", StringComparison.OrdinalIgnoreCase));
                                        if (inbox != null)
                                        {
                                            var created = await graph.Users[userId].MailFolders[inbox.Id].ChildFolders.PostAsync(new MailFolder { DisplayName = "Processed" });
                                            processedFolder = created ?? processedFolder;
                                        }
                                    }
                                    catch (Exception ex)
                                    {
                                        _log.LogWarning(ex, "Failed to create Processed folder.");
                                    }
                                }
                                if (processedFolder != null)
                                {
                                    await graph.Users[userId].Messages[messageId].Move.PostAsync(new Microsoft.Graph.Users.Item.Messages.Item.Move.MovePostRequestBody { DestinationId = processedFolder.Id });
                                    try { await _audit.WriteAsync("message_moved", e => { e["messageId"] = messageId; e["folderId"] = processedFolder.Id; }); } catch { }
                                }
                            }
                            else
                            {
                                try { await _audit.WriteAsync("no_device_mapping", e => { e["subject"] = subject; }); } catch { }
                            }
                        }
                        catch (Exception ex)
                        {
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
            if (subject.IndexOf("DISPATCH-1", StringComparison.OrdinalIgnoreCase) >= 0) return "AlertV1-Dev1"; // default mapping
            if (subject.IndexOf("DISPATCH-2", StringComparison.OrdinalIgnoreCase) >= 0) return "AlertV1-Dev2";
            if (subject.IndexOf("DISPATCH-3", StringComparison.OrdinalIgnoreCase) >= 0) return "AlertV1-Dev3";
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