using System.Text.Json;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Azure.Functions.Worker.Http;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Configuration;
using cad_dispatch.Services;

namespace cad_dispatch.Functions
{
    public class TestHarness
    {
        private readonly IoTHubService _iot;
        private readonly AuditLogService _audit;
        private readonly IConfiguration _config;
        private readonly ILogger<TestHarness> _log;
        public TestHarness(IoTHubService iot, AuditLogService audit, IConfiguration config, ILogger<TestHarness> log)
        {
            _iot = iot; _audit = audit; _config = config; _log = log;
        }

        [Function("TestRelay")]
        public async Task<HttpResponseData> Run([HttpTrigger(AuthorizationLevel.Anonymous, "get")] HttpRequestData req)
        {
            var enabled = bool.TryParse(_config["Features__DispatchEnabled"], out var en) ? en : true;
            var q = System.Web.HttpUtility.ParseQueryString(req.Url.Query);
            var deviceId = q.Get("deviceId");
            var relay = q.Get("relay");
            if (string.IsNullOrEmpty(deviceId) && !string.IsNullOrEmpty(relay))
            {
                deviceId = relay switch
                {
                    "1" => "Relay-1",
                    "2" => "Relay-2",
                    "3" => "Relay-3",
                    _ => null
                };
            }

            var res = req.CreateResponse();
            res.Headers.Add("Content-Type", "application/json");

            if (!enabled)
            {
                res.StatusCode = System.Net.HttpStatusCode.Forbidden;
                await res.WriteStringAsync("{\"error\":\"dispatch_disabled\"}");
                return res;
            }
            if (string.IsNullOrEmpty(deviceId))
            {
                res.StatusCode = System.Net.HttpStatusCode.BadRequest;
                await res.WriteStringAsync("{\"error\":\"deviceId_or_relay_required\"}");
                return res;
            }

            var payload = new { subject = $"TEST-DISPATCH-{(relay ?? "?")}", reason = "HTTP" };
            var result = await _iot.TriggerRelayAsync(deviceId, payload);
            _log.LogInformation("TestRelay invoked for {DeviceId} via {Via}", deviceId, result.via);
            await _audit.WriteAsync("test_relay", e => { e["deviceId"] = deviceId; e["via"] = result.via; e["status"] = result.status; });

            res.StatusCode = System.Net.HttpStatusCode.OK;
            await res.WriteStringAsync(JsonSerializer.Serialize(new { deviceId, result.via, result.status }));
            return res;
        }
    }
}
