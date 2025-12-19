
// ConfigProbe.cs - HTTP probe to read runtime configuration
using System.Text.Json;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Azure.Functions.Worker.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace cad_dispatch.Functions
{
    public class ConfigProbe
    {
        private readonly IConfiguration _config;
        private readonly ILogger<ConfigProbe> _log;

        public ConfigProbe(IConfiguration config, ILogger<ConfigProbe> log)
        {
            _config = config;
            _log = log;
        }

        [Function("ConfigProbe")]
        public async Task<HttpResponseData> Run([HttpTrigger(AuthorizationLevel.Anonymous, "get")] HttpRequestData req)
        {
            var res = req.CreateResponse();
            res.Headers.Add("Content-Type", "application/json");

            var payload = new
            {
                Storage_TableName = _config["Storage:TableName"],
                Storage_AccountUri = _config["Storage:AccountUri"],
                Storage_ConnectionString_Present = string.IsNullOrWhiteSpace(_config["Storage:ConnectionString"]) ? false : true,
                IoTHub_ConnectionString_Present = string.IsNullOrWhiteSpace(_config["IoTHub:ConnectionString"]) ? false : true,
                AppConfig_Endpoint = _config["AppConfig__Endpoint"],
                AppConfig_ConnectionString_Present = string.IsNullOrWhiteSpace(_config["AppConfig__ConnectionString"]) ? false : true
            };

            _log.LogInformation("ConfigProbe: Storage:TableName={Table}, AccountUri={Uri}, IoTHub CS present={IoT}",
                payload.Storage_TableName, payload.Storage_AccountUri, payload.IoTHub_ConnectionString_Present);

            await res.WriteStringAsync(JsonSerializer.Serialize(payload));
            return res;
        }
    }
}
