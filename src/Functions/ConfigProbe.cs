// ConfigProbe.cs - HTTP probe with raw environment inspection
using System;
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

            // Read both configuration and raw environment variables to isolate issues
            var payload = new
            {
                // From IConfiguration
                Storage_TableName = _config["Storage:TableName"],
                Storage_AccountUri = _config["Storage:AccountUri"],
                Storage_ConnectionString_Present = !string.IsNullOrWhiteSpace(_config["Storage:ConnectionString"]),
                IoTHub_ConnectionString_Present = !string.IsNullOrWhiteSpace(_config["IoTHub:ConnectionString"]),
                AppConfig_Endpoint = _config["AppConfig__Endpoint"],
                AppConfig_ConnectionString_Present = !string.IsNullOrWhiteSpace(_config["AppConfig__ConnectionString"]),

                // Raw environment variables (Linux is case-sensitive)
                Env_AppConfig_Endpoint = Environment.GetEnvironmentVariable("AppConfig__Endpoint"),
                Env_AppConfig_Conn = Environment.GetEnvironmentVariable("AppConfig__ConnectionString"),
                Env_Storage_TableName = Environment.GetEnvironmentVariable("Storage__TableName"),
                Env_Storage_AccountUri = Environment.GetEnvironmentVariable("Storage__AccountUri"),
                Env_Storage_Conn = Environment.GetEnvironmentVariable("Storage__ConnectionString"),
                Env_IoTHub_Conn = Environment.GetEnvironmentVariable("IoTHub__ConnectionString"),

                // Worker/runtime basics
                FUNCTIONS_WORKER_RUNTIME = Environment.GetEnvironmentVariable("FUNCTIONS_WORKER_RUNTIME"),
                WEBSITE_SITE_NAME = Environment.GetEnvironmentVariable("WEBSITE_SITE_NAME"),
                WEBSITE_SLOT_NAME = Environment.GetEnvironmentVariable("WEBSITE_SLOT_NAME")
            };

            _log.LogInformation("ConfigProbe: TableName={Table}, AccountUri={Uri}, IoT CS={IoT}, AppConfig Endpoint (cfg)={ACE}, (env)={ACE2}",
                payload.Storage_TableName, payload.Storage_AccountUri, payload.IoTHub_ConnectionString_Present,
                payload.AppConfig_Endpoint, payload.Env_AppConfig_Endpoint);

            await res.WriteStringAsync(JsonSerializer.Serialize(payload));
            return res;
        }
    }
}
