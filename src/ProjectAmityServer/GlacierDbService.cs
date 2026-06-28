using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Glacier.Polaris;
using Glacier.Polaris.Data;
using Glacier.Sql.Catalog;
using Glacier.Sql.Engine;

namespace ProjectAmityServer
{
    public class GlacierDbService
    {
        private readonly CatalogManager _catalog;
        private readonly SqlEngine _engine;
        private readonly Glacier.Sql.Engine.ExecutionContext _context;
        private readonly ILogger<GlacierDbService> _logger;
        private readonly string _dataDir;
        private readonly System.Threading.SemaphoreSlim _lock = new System.Threading.SemaphoreSlim(1, 1);

        public GlacierDbService(IConfiguration configuration, ILogger<GlacierDbService> logger)
        {
            _logger = logger;
            
            // Put database data inside a folder called "Database" under AppData
            string appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
            _dataDir = Path.Combine(appData, "ProjectAmity", "Database");
            
            _logger.LogInformation($"Initializing GlacierDbService at: {_dataDir}");
            _catalog = new CatalogManager(_dataDir);
            _engine = new SqlEngine(_catalog);
            _context = new Glacier.Sql.Engine.ExecutionContext(_catalog);
        }

        // Helper to format/replace SQL parameters
        public string BindParameters(string sql, Dictionary<string, object?>? parameters)
        {
            if (parameters == null || parameters.Count == 0) return sql;

            // Sort parameters by length descending to avoid replacing partial names (e.g. replacing @Id before @Id2)
            var sortedParams = parameters.OrderByDescending(p => p.Key.Length).ToList();

            foreach (var param in sortedParams)
            {
                string placeholder = param.Key;
                if (!placeholder.StartsWith("@")) placeholder = "@" + placeholder;

                object? val = param.Value;
                string replacement;

                if (val == null || val is DBNull)
                {
                    replacement = "NULL";
                }
                else if (val is string str)
                {
                    replacement = "'" + str.Replace("'", "''") + "'";
                }
                else if (val is bool b)
                {
                    replacement = b ? "1" : "0";
                }
                else if (val is DateTime dt)
                {
                    // Convert DateTime to unix seconds to avoid Int32 overflow in Glacier.Sql
                    long unixSecs = new DateTimeOffset(dt).ToUnixTimeSeconds();
                    replacement = unixSecs.ToString();
                }
                else if (val is double || val is float || val is int || val is long || val is decimal)
                {
                    replacement = val.ToString()!;
                }
                else
                {
                    replacement = "'" + val.ToString()!.Replace("'", "''") + "'";
                }

                sql = sql.Replace(placeholder, replacement);
            }

            return sql;
        }

        // Run DDL/DML statements
        public async Task<int> ExecuteNonQueryAsync(string sql, Dictionary<string, object?>? parameters = null)
        {
            string boundSql = BindParameters(sql, parameters);
            _logger.LogDebug($"Executing SQL: {boundSql}");
            
            await _lock.WaitAsync();
            try
            {
                var result = await _engine.ExecuteAsync(boundSql, _context);
                if (!result.Success)
                {
                    _logger.LogError($"Glacier.Sql Error executing query: {result.Message}\nSQL: {boundSql}");
                    throw new Exception(result.Message);
                }
                return result.AffectedRows;
            }
            finally
            {
                _lock.Release();
            }
        }

        // Run query that returns a single value
        public async Task<object?> ExecuteScalarAsync(string sql, Dictionary<string, object?>? parameters = null)
        {
            var list = await ExecuteQueryAsync(sql, parameters);
            if (list.Count > 0)
            {
                return list[0].Values.FirstOrDefault();
            }
            return null;
        }

        // Run query that returns multiple rows
        public async Task<List<Dictionary<string, object?>>> ExecuteQueryAsync(string sql, Dictionary<string, object?>? parameters = null)
        {
            string boundSql = BindParameters(sql, parameters);
            _logger.LogDebug($"Querying SQL: {boundSql}");

            await _lock.WaitAsync();
            try
            {
                var result = await _engine.ExecuteAsync(boundSql, _context);
                if (!result.Success)
                {
                    _logger.LogError($"Glacier.Sql Error executing query: {result.Message}\nSQL: {boundSql}");
                    throw new Exception(result.Message);
                }

                var rows = new List<Dictionary<string, object?>>();
                if (result.DataFrame != null)
                {
                    var df = result.DataFrame;
                    int rowCount = df.RowCount;
                    var columns = df.Columns;

                    for (int r = 0; r < rowCount; r++)
                    {
                        var row = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
                        foreach (var col in columns)
                        {
                            object? val = col.Get(r);
                            
                            // Convert unix timestamp back to DateTime if it's mapped to DATETIME column
                            long? secVal = null;
                            if (val is int iVal) secVal = iVal;
                            else if (val is long lVal) secVal = lVal;

                            if (secVal.HasValue)
                            {
                                var tableMeta = _catalog.GetTable(GetTableNameFromQuery(sql));
                                var colMeta = tableMeta?.Columns.FirstOrDefault(c => c.Name.Equals(col.Name, StringComparison.OrdinalIgnoreCase));
                                if (colMeta != null && colMeta.DataType.Equals("DATETIME", StringComparison.OrdinalIgnoreCase))
                                {
                                    if (secVal.Value > 100000000000L)
                                    {
                                        val = DateTimeOffset.FromUnixTimeMilliseconds(secVal.Value).LocalDateTime;
                                    }
                                    else
                                    {
                                        val = DateTimeOffset.FromUnixTimeSeconds(secVal.Value).LocalDateTime;
                                    }
                                }
                            }
                            
                            row[col.Name] = val;
                        }
                        rows.Add(row);
                    }
                }

                return rows;
            }
            finally
            {
                _lock.Release();
            }
        }

        // Helper to extract table name from query for datetime conversions
        private string GetTableNameFromQuery(string sql)
        {
            var tokens = sql.Split(new[] { ' ', '\n', '\r', '\t' }, StringSplitOptions.RemoveEmptyEntries);
            int fromIdx = Array.FindIndex(tokens, t => t.Equals("FROM", StringComparison.OrdinalIgnoreCase));
            if (fromIdx != -1 && fromIdx + 1 < tokens.Length)
            {
                string name = tokens[fromIdx + 1].Trim('[', ']', '"', '\'');
                int dotIdx = name.IndexOf('.');
                if (dotIdx != -1) name = name.Substring(dotIdx + 1);
                return name;
            }
            return "";
        }

        // Helper to get next auto-incrementing ID for a table
        public async Task<int> GetNextIdAsync(string tableName)
        {
            var res = await ExecuteScalarAsync($"SELECT MAX(Id) FROM {tableName}");
            if (res == null || res is DBNull) return 1;
            
            if (res is int i) return i + 1;
            if (res is long l) return (int)l + 1;
            if (res is double d) return (int)d + 1;

            return 1;
        }

        public async Task<int> UpdateRowAsync(string tableName, int id, Dictionary<string, object?> updatedValues)
        {
            // 1. Get the current row
            var query = $"SELECT * FROM {tableName} WHERE Id = {id}";
            var rows = await ExecuteQueryAsync(query);
            if (rows.Count == 0) return 0;

            var row = rows[0];

            // 2. Delete the row
            var deleteQuery = $"DELETE FROM {tableName} WHERE Id = {id}";
            await ExecuteNonQueryAsync(deleteQuery);

            // 3. Merge updated values
            foreach (var kvp in updatedValues)
            {
                row[kvp.Key] = kvp.Value;
            }

            // 4. Perform insert
            var columns = new List<string>();
            var valuePlaceholders = new List<string>();
            var insertParams = new Dictionary<string, object?>();

            foreach (var kvp in row)
            {
                columns.Add(kvp.Key);
                valuePlaceholders.Add("@" + kvp.Key);
                insertParams.Add("@" + kvp.Key, kvp.Value);
            }

            var insertQuery = $"INSERT INTO {tableName} ({string.Join(", ", columns)}) VALUES ({string.Join(", ", valuePlaceholders)})";
            return await ExecuteNonQueryAsync(insertQuery, insertParams);
        }

        public CatalogManager Catalog => _catalog;
    }
}
