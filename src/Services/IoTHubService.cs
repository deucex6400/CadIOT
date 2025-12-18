
using System.Text;
using System.Text.Json;
using Azure.Identity;
using Microsoft.Azure.Devices;
using Microsoft.Azure.Devices.Common.Exceptions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace cad_dispatch.Services
{
    /// <summary>
    /// Sends commands to ESP32 devices via IoT Hub.
    /// Prefers AAD/MSI when IoTHub:HostName is set; otherwise uses IoTHub:ConnectionString.
    /// Works with either App Config (colon keys) or env vars (double-underscore).
    /// </summary>
    public class IoTHubService
    {
        private static ServiceClient? _cachedClient;
        private static readonly object _lock = new();

        private readonly IConfiguration _config;
        private readonly ILogger<IoTHubService>? _log;

        public IoTHubService(IConfiguration config, ILogger<IoTHubService>? log = null)
        {
            _config = config;
            _log = log;
        }

        // Unified reads: section -> colon -> double underscore
        private string? Get(string key)
        {
            var section = _config.GetSection("IoTHub");
            return section[key] ?? _config[$"IoTHub:{key}"] ?? _config[$"IoTHub__{key}"];
        }

        private async Task<ServiceClient> GetClientAsync()
        {
            if (_cachedClient is not null)
                return _cachedClient;

            lock (_lock)
            {
                if (_cachedClient is null)
                {
                    var hostName = Get("HostName");           // e.g., bgvfd-iot-hub.azure-devices.net
                    var connStr = Get("ConnectionString");   // Hub connection string (service policy)

                    _log?.LogInformation("IoTHub auth path: {path}", string.IsNullOrWhiteSpace(hostName) ? "connectionString" : "AAD/MSI");



                    if (!string.IsNullOrWhiteSpace(hostName))
                    {
                        // AAD/MSI path: requires IoT Hub RBAC (e.g., IoT Hub Data Contributor)
                        // ServiceClient.Create(hostName, TokenCredential) is supported.
                        _cachedClient = ServiceClient.Create(
                            hostName,
                            new DefaultAzureCredential()
                        ); // Transport/Options can be provided if needed
                    }
                    else if (!string.IsNullOrWhiteSpace(connStr))
                    {
                        _cachedClient = ServiceClient.CreateFromConnectionString(connStr);
                    }
                    else
                    {
                        throw new InvalidOperationException(
                            "IoTHub configuration missing. Supply IoTHub:HostName (for MSI) or IoTHub:ConnectionString.");
                    }
                }
            }


            //var hostName = Get("HostName");
            //var connStr = Get("ConnectionString");
            //_log?.LogInformation("IoTHub auth path: {path}", string.IsNullOrWhiteSpace(hostName) ? "connectionString" : "AAD/MSI");

            await _cachedClient!.OpenAsync();
            return _cachedClient!;
        }

        public async Task<(string via, int status)> TriggerRelayAsync(string deviceId, object payload)
        {
            var client = await GetClientAsync();

            // Try a direct method first
            var method = new CloudToDeviceMethod("activateRelay")
            {
                ResponseTimeout = TimeSpan.FromSeconds(6),
                ConnectionTimeout = TimeSpan.FromSeconds(6)
            };
            method.SetPayloadJson(JsonSerializer.Serialize(payload));

            try
            {
                var dmResult = await client.InvokeDeviceMethodAsync(deviceId, method);
                _log?.LogInformation("Direct method status={Status}", dmResult.Status);
                return ("directMethod", dmResult.Status);
            }
            catch (DeviceNotFoundException) { _log?.LogWarning("Device not found: {DeviceId}", deviceId); }
            catch (IotHubCommunicationException) { _log?.LogWarning("IoT Hub comm error for {DeviceId}", deviceId); }
            catch (UnauthorizedException) { _log?.LogError("Unauthorized invoking method on {DeviceId}"); }
            catch (Exception ex) { _log?.LogError(ex, "Method invoke failed for {DeviceId}", deviceId); }

            // Fallback to C2D message
            var msgBody = JsonSerializer.Serialize(new { cmd = "activateRelay", payload });
            var message = new Message(Encoding.UTF8.GetBytes(msgBody))
            {
                Ack = DeliveryAcknowledgement.Full,
                ExpiryTimeUtc = DateTime.UtcNow.AddSeconds(60)
            };
            message.Properties["source"] = "CAD";
            message.Properties["command"] = "activateRelay";

            await client.SendAsync(deviceId, message);
            _log?.LogInformation("Sent C2D to {DeviceId}", deviceId);
            return ("c2d", 202);
        }
    }
}
