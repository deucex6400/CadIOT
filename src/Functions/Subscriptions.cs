// Subscriptions.cs — HTTP GET endpoint to list Microsoft Graph change notification subscriptions
// .NET 8 isolated worker
using Microsoft.Azure.Functions.Worker;
using Microsoft.Azure.Functions.Worker.Http;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Configuration;
using System.Net;
using System.Text.Json;
using cad_dispatch.Services;

namespace cad_dispatch.Functions
{
    public class Subscriptions
    {
        private readonly GraphClientFactory _graphFactory;
        private readonly IConfiguration _config;
        private readonly ILogger<Subscriptions> _log;

        public Subscriptions(GraphClientFactory graphFactory, IConfiguration config, ILogger<Subscriptions> log)
        {
            _graphFactory = graphFactory;
            _config = config;
            _log = log;
        }

        [Function("Subscriptions")]
        public async Task<HttpResponseData> Run(
            [HttpTrigger(AuthorizationLevel.Function, "get", Route = "Subscriptions")] HttpRequestData req)
        {
            var res = req.CreateResponse(HttpStatusCode.OK);
            res.Headers.Add("Content-Type", "application/json");

            try
            {
                var graph = _graphFactory.Client;
                var collection = await graph.Subscriptions.GetAsync();
                var subs = collection?.Value ?? new List<Microsoft.Graph.Models.Subscription>();

                var payload = subs.Select(s => new
                {
                    id = s.Id,
                    resource = s.Resource,
                    changeType = s.ChangeType,
                    notificationUrl = s.NotificationUrl,
                    lifecycleNotificationUrl = s.LifecycleNotificationUrl,
                    includeResourceData = s.IncludeResourceData,
                    encryptionCertificateId = s.EncryptionCertificateId,
                    expirationDateTime = s.ExpirationDateTime,
                    clientState = s.ClientState
                });

                await res.WriteStringAsync(JsonSerializer.Serialize(new
                {
                    count = subs.Count,
                    items = payload
                }));
            }
            catch (Exception ex)
            {
                _log.LogError(ex, "Failed to list Graph subscriptions.");
                res = req.CreateResponse(HttpStatusCode.InternalServerError);
                res.Headers.Add("Content-Type", "application/json");
                await res.WriteStringAsync(JsonSerializer.Serialize(new { error = ex.Message }));
            }

            return res;
        }
    }
}
