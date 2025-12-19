using Azure;
using Azure.Data.Tables;
using Azure.Identity;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace cad_dispatch.Services
{
    public class AuditLogService
    {
        private readonly TableClient _table;
        //private const string DefaultTableName = "DispatchAudit";

        public AuditLogService(IConfiguration config, ILogger<AuditLogService>? logger = null)
        {
            // Unify keys from both sources:
            // - Azure App Configuration: Storage:AccountUri / Storage:ConnectionString / Storage:TableName
            // - Env vars: Storage__AccountUri / Storage__ConnectionString / Storage__TableName
            var storage = config.GetSection("Storage");
            var tableName = storage["TableName"]
                         ?? config["Storage:TableName"]
                         ?? config["Storage__TableName"];
            //?? DefaultTableName;
            var connStr = storage["ConnectionString"]
                         ?? config["Storage:ConnectionString"]
                         ?? config["Storage__ConnectionString"];
            var accountUri = storage["AccountUri"]
                         ?? config["Storage:AccountUri"]
                         ?? config["Storage__AccountUri"];

            logger?.LogInformation("[AuditLogService] TableName={Table}, ConnStr={HasConn}, AccountUri={HasUri}",
                                   tableName,
                                   string.IsNullOrWhiteSpace(connStr) ? "no" : "yes",
                                   string.IsNullOrWhiteSpace(accountUri) ? "no" : "yes");

            if (!string.IsNullOrWhiteSpace(connStr))
            {
                _table = new TableClient(connStr, tableName);
            }
            else if (!string.IsNullOrWhiteSpace(accountUri))
            {
                var uri = new Uri(accountUri); // e.g., https://<account>.table.core.windows.net
                _table = new TableClient(uri, tableName, new DefaultAzureCredential());
            }
            else
            {
                // Fail fast in cloud: missing config
                throw new InvalidOperationException(
                    "AuditLogService configuration missing. Supply Storage:ConnectionString or Storage:AccountUri.");
            }

            // Create table if needed (retry for transient issues)
            CreateTableIfNotExistsAsync().GetAwaiter().GetResult();
        }

        private async Task CreateTableIfNotExistsAsync()
        {
            const int maxAttempts = 3;
            for (var attempt = 1; attempt <= maxAttempts; attempt++)
            {
                try
                {
                    await _table.CreateIfNotExistsAsync();
                    return;
                }
                catch (RequestFailedException) when (attempt < maxAttempts)
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
