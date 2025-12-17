using System;
using System.Threading.Tasks;
using Azure.Data.Tables;
using Azure.Identity;
using Microsoft.Extensions.Configuration;

namespace cad_dispatch.Services
{
    /// <summary>
    /// Writes structured audit events to Azure Table Storage.
    /// Reads Storage settings from IConfiguration (Azure App Configuration or local settings).
    /// </summary>
    public class AuditLogService
    {
        private readonly TableClient _table;
        private const string DefaultTableName = "DispatchAudit";

        public AuditLogService(IConfiguration config)
        {
            var tableName = config["Storage__TableName"] ?? DefaultTableName;
            var connStr   = config["Storage__ConnectionString"];
            var accountUri= config["Storage__AccountUri"];

            if (!string.IsNullOrEmpty(connStr))
            {
                _table = new TableClient(connStr, tableName);
            }
            else if (!string.IsNullOrEmpty(accountUri))
            {
                var uri = new Uri(accountUri);
                _table = new TableClient(uri, tableName, new DefaultAzureCredential());
            }
            else
            {
                // local dev fallback
                _table = new TableClient("UseDevelopmentStorage=true", tableName);
            }

            _table.CreateIfNotExists();
        }

        public async Task WriteAsync(string eventType, Action<TableEntity> enrich)
        {
            var entity = new TableEntity
            {
                PartitionKey = DateTime.UtcNow.ToString("yyyyMMdd"),
                RowKey = Guid.NewGuid().ToString(),
            };
            entity["eventType"]   = eventType;
            entity["timestampUtc"] = DateTime.UtcNow;
            enrich?.Invoke(entity);
            await _table.AddEntityAsync(entity);
        }
    }
}
