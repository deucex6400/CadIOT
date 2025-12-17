using System.Text;
using System.Text.Json;
using Azure.Identity;
using Microsoft.Azure.Devices;
using Microsoft.Azure.Devices.Common.Exceptions;
using Microsoft.Extensions.Configuration;

namespace cad_dispatch.Services
{
    /// <summary>
    /// Sends commands to ESP32 devices via IoT Hub.
    /// Chooses AAD/MSI when IoTHub__HostName is set; otherwise uses IoTHub__ConnectionString.
    /// </summary>
    public class IoTHubService
    {
        private static ServiceClient? _cachedClient;
        private static readonly object _lock = new();
        private readonly string? _hubHostname;
        private readonly string? _hubConnStr;

        public IoTHubService(IConfiguration config)
        {
            _hubHostname = config["IoTHub__HostName"];
            _hubConnStr  = config["IoTHub__ConnectionString"];
        }

        private async Task<ServiceClient> GetClientAsync()
        {
            if (_cachedClient is not null)
                return _cachedClient;

            lock (_lock)
            {
                if (_cachedClient is null)
                {
                    if (!string.IsNullOrEmpty(_hubHostname))
                    {
                        _cachedClient = ServiceClient.Create(_hubHostname, new DefaultAzureCredential());
                    }
                    else
                    {
                        _cachedClient = ServiceClient.CreateFromConnectionString(_HubConnStrOrThrow());
                    }
                }
            }

            await _cachedClient!.OpenAsync();
            return _cachedClient!;
        }

        private string _HubConnStrOrThrow()
        {
            if (string.IsNullOrEmpty(_hubConnStr))
                throw new InvalidOperationException("IoTHub__ConnectionString is not configured.");
            return _hubConnStr!;
        }

        public async Task<(string via, int status)> TriggerRelayAsync(string deviceId, object payload)
        {
            var client = await GetClientAsync();

            var method = new CloudToDeviceMethod("activateRelay")
            {
                ResponseTimeout   = TimeSpan.FromSeconds(6),
                ConnectionTimeout = TimeSpan.FromSeconds(6)
            };
            method.SetPayloadJson(JsonSerializer.Serialize(payload));

            try
            {
                var dmResult = await client.InvokeDeviceMethodAsync(deviceId, method);
                return ("directMethod", dmResult.Status);
            }
            catch (DeviceNotFoundException) { }
            catch (IotHubCommunicationException) { }
            catch (UnauthorizedException) { }
            catch { }

            var msgBody = JsonSerializer.Serialize(new { cmd = "activateRelay", payload });
            var message = new Message(Encoding.UTF8.GetBytes(msgBody))
            {
                Ack = DeliveryAcknowledgement.Full,
                ExpiryTimeUtc = DateTime.UtcNow.AddSeconds(60)
            };
            message.Properties["source"]  = "CAD";
            message.Properties["command"] = "activateRelay";

            await client.SendAsync(deviceId, message);
            return ("c2d", 202);
        }
    }
}
