
using System;
using System.Threading.Tasks;
using Azure;
using Azure.Data.Tables;
using Azure.Identity;
using Microsoft.Extensions.Configuration;

namespace cad_dispatch.Services
{
    /// <summary>
    /// Writes structured audit events to Azure Table Storage.
    /// Reads settings from IConfiguration (Azure App Configuration or local settings).
    /// </summary>
    public class AuditLogService
    {
        private readonly TableClient _table;
        private const string DefaultTableName = "DispatchAudit";

        public AuditLogService(IConfiguration config)
        {
            var tableName = config["Storage__TableName"] ?? DefaultTableName;
            var connStr = config["Storage__ConnectionString"];
            var accountUri = config["Storage__AccountUri"];

            if (!string.IsNullOrWhiteSpace(connStr))
            {
                // Storage account (keys/SAS) connection string
                _table = new TableClient(connStr, tableName);
            }
            else if (!string.IsNullOrWhiteSpace(accountUri))
            {
                // Managed Identity / Azure AD path
                var uri = new Uri(accountUri); // e.g. https://<account>.table.core.windows.net
                var credential = new DefaultAzureCredential(
                    new DefaultAzureCredentialOptions
                    {
                        // (Optional) helpful when debugging locally to use VisualStudio/CLI auth:
                        // ExcludeInteractiveBrowserCredential = false,
                    });
                _table = new TableClient(uri, tableName, credential);
            }
            else
            {
                // NO dev fallback in cloud: throw with guidance
                // If you still want local Azurite, put it in appsettings.Development.json only.
                throw new InvalidOperationException(
                    "AuditLogService configuration missing. Set either Storage__ConnectionString " +
                    "or Storage__AccountUri (preferred with Managed Identity).");
            }

            // Create table if needed (async recommended)
            CreateTableIfNotExistsAsync().GetAwaiter().GetResult();
        }

        private async Task CreateTableIfNotExistsAsync()
        {
            // Simple retry for transient failures
            const int maxAttempts = 3;
            int attempt = 0;
            while (true)
            {
                try
                {
                    await _table.CreateIfNotExistsAsync();
                    return;
                }
                catch (RequestFailedException) when (++attempt < maxAttempts)
                {
                    await Task.Delay(TimeSpan.FromSeconds(2 * attempt));
                }
            }
        }

        public async Task WriteAsync(string eventType, Action<TableEntity> enrich)
        {
            var entity = new TableEntity
            {
                PartitionKey = DateTime.UtcNow.ToString("yyyyMMdd"),
                RowKey = Guid.NewGuid().ToString(),
            };
            entity["eventType"] = eventType;
            entity["timestampUtc"] = DateTime.UtcNow;

            enrich?.Invoke(entity);
            await _table.AddEntityAsync(entity);
        }
    }
}