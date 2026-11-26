
using System.Data;
using System.Security.Cryptography;
using System.Text.RegularExpressions;
using Npgsql;
using NpgsqlTypes;
using JsonLoader.Enums;

namespace JsonLoader;

public sealed partial class PostgresTableLoader(
    string connectionString,
    string schemaName = "game_data",
    RemovedColumnPolicy removedColumnPolicy = RemovedColumnPolicy.DropNotNull)
{
    private const string SystemPkColumn = "__row_id";
    private readonly string _connectionString = connectionString;
    private readonly string _schemaName = SanitizeIdentifier(schemaName);
    private readonly RemovedColumnPolicy _removedColumnPolicy = removedColumnPolicy;

    private sealed record ExistingColumnInfo(string DataType, bool IsNullable);

    public async Task SyncAndLoadAsync(
        string rawTableName,
        Dictionary<string, ColumnData> columns,
        CancellationToken ct = default)
    {
        if (columns.Count == 0) return;
        var tableName = SanitizeIdentifier(rawTableName);
        if (columns.Keys.Any(k =>
                string.Equals(SanitizeIdentifier(k), SystemPkColumn, StringComparison.InvariantCultureIgnoreCase)))
        {
            throw new InvalidOperationException($"Forbidden column name({SystemPkColumn}) used in table {tableName}");
        }

        var columnList = columns.Values.ToList();
        var rowCount = columnList[0].Values.Count;
        
        await using var conn = new NpgsqlConnection(_connectionString);
        await conn.OpenAsync(ct);
        
        await using var tx = await conn.BeginTransactionAsync(ct);
        try
        {
            await ExecuteNonQueryAsync(conn, tx, $"CREATE SCHEMA IF NOT EXISTS {Quote(_schemaName)};", ct);
            var existingColumns = await GetExistingColumnsAsync(conn, tx, tableName, ct);
            if (existingColumns.Count == 0)
            {
                await CreateTableAsync(conn, tx, tableName, columnList, ct);
            }
            else
            {
                var dropSql = $"TRUNCATE TABLE {Quote(_schemaName)}.{Quote(tableName)} RESTART IDENTITY";
                await ExecuteNonQueryAsync(conn, tx, dropSql, ct);
                await SyncExistingTableSchemaAsync(conn, tx, tableName, columnList, existingColumns, ct);
            }

            if (rowCount > 0)
            {

            }

            await tx.CommitAsync(ct);
        }
        catch
        {
            await tx.RollbackAsync(ct);
            throw;
        }
    }

    private async Task BulkInsertAsync(
        NpgsqlConnection conn,
        string tableName,
        List<ColumnData> columns, 
        int rowCount,
        CancellationToken ct = default)
    {
        var colNames = string.Join(", ", columns.Select(c => Quote(SanitizeIdentifier(c.Name))));
        var copySql = $"COPY {Quote(_schemaName)}.{Quote(tableName)} ({colNames}) FROM STDIN (FORMAT BINARY)";
        await using var writer = await conn.BeginBinaryImportAsync(copySql, ct);
        for (int rowIdx = 0; rowIdx < rowCount; rowIdx++)
        {
            await writer.StartRowAsync(ct);
            foreach (var col in columns)
            {
                var value = col.Values[rowIdx];
                if(value is null) await writer.WriteNullAsync(ct);
                else await writer.WriteAsync(value, MapToNpgsqlDbType(col.Type), ct);
            }
        }
        await writer.CompleteAsync(ct);
    }

    private async Task SyncExistingTableSchemaAsync(
        NpgsqlConnection conn,
        NpgsqlTransaction tx,
        string tableName,
        List<ColumnData> incomingColumns,
        Dictionary<string, ExistingColumnInfo> existingColumns,
        CancellationToken ct = default)
    {
        var fullTableName = $"{Quote(_schemaName)}.{Quote(tableName)}";
        var incomingNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var col in incomingColumns)
        {
            var colName = SanitizeIdentifier(col.Name);
            incomingNames.Add(colName);
            var targetPgType = MapToPgDataType(col.Type);
            if (!existingColumns.TryGetValue(colName, out var existing))
            {
                var nullability = col.IsNullable ? "NULL" : "NOT NULL";
                var addSql = $"ALTER TABLE {fullTableName} ADD COLUMN {Quote(colName)} {targetPgType} {nullability};";
                await ExecuteNonQueryAsync(conn, tx, addSql, ct);
                continue;
            }

            if (!string.Equals(existing.DataType, targetPgType, StringComparison.OrdinalIgnoreCase))
            {
                var alterTypeSql =
                    $"ALTER TABLE {fullTableName} ALTER COLUMN {Quote(colName)} TYPE {targetPgType} USING NULL::{targetPgType};";
                await ExecuteNonQueryAsync(conn, tx, alterTypeSql, ct);
            }

            if (existing.IsNullable != col.IsNullable)
            {
                var nullAction = col.IsNullable ? "DROP NOT NULL" : "SET NOT NULL";
                var alterNullSql = $"ALTER TABLE {fullTableName} ALTER COLUMN {Quote(colName)} {nullAction};";
                await ExecuteNonQueryAsync(conn, tx, alterNullSql, ct);
            }
        }

        foreach (var (colName, existing) in existingColumns)
        {
            if (string.Equals(colName, SystemPkColumn, StringComparison.OrdinalIgnoreCase) || incomingNames.Contains(colName))
            {
                continue;
            }

            if (_removedColumnPolicy == RemovedColumnPolicy.DropColumn)
            {
                await ExecuteNonQueryAsync(conn, tx, $"ALTER TABLE {fullTableName} DROP COLUMN {Quote(colName)};", ct);
            }
            else
            {
                await ExecuteNonQueryAsync(conn, tx, $"ALTER TABLE {fullTableName} ALTER COLUMN {Quote(colName)} DROP NOT NULL;", ct);
            }
        }
    }

    private async Task CreateTableAsync(
        NpgsqlConnection conn,
        NpgsqlTransaction tx,
        string tableName,
        List<ColumnData> columns,
        CancellationToken ct = default)
    {
        var pkDef = $"{Quote(SystemPkColumn)} bigint GENERATED ALWAYS AS IDENTITY PRIMARY KEY";
        var colDefs = columns.Select(col =>
        {
            var colName = Quote(SanitizeIdentifier(col.Name));
            var pgType = MapToPgDataType(col.Type);
            var nullability = col.IsNullable ? "NULL" : "NOT NULL";
            return $"{colName} {pgType} {nullability}";
        });
        var allDefs = new[] {pkDef}.Concat(colDefs);
        var sql = $"CREATE TABLE {Quote(_schemaName)}.{Quote(tableName)} ({string.Join(", ", allDefs)});";
        await ExecuteNonQueryAsync(conn, tx, sql, ct);
    }

    private static string MapToPgDataType(ColumnType type) => type switch
    {
        ColumnType.Boolean => "boolean",
        ColumnType.Integer => "bigint",
        ColumnType.Float => "real",
        ColumnType.Double => "double precision",
        ColumnType.Json => "jsonb",
        _ => "text"
    };

    private static NpgsqlDbType MapToNpgsqlDbType(ColumnType type) => type switch
    {
        ColumnType.Boolean => NpgsqlDbType.Boolean,
        ColumnType.Integer => NpgsqlDbType.Bigint,
        ColumnType.Float => NpgsqlDbType.Real,
        ColumnType.Double => NpgsqlDbType.Double,
        ColumnType.Json => NpgsqlDbType.Jsonb,
        _ => NpgsqlDbType.Text
    };

    private async Task<Dictionary<string, ExistingColumnInfo>> GetExistingColumnsAsync(
        NpgsqlConnection conn,
        NpgsqlTransaction tx,
        string tableName,
        CancellationToken ct = default)
    {
        const string sql = "SELECT column_name, data_type, is_nullable FROM information_schema.columns WHERE table_schema = @schema AND table_name = @table";
        var result = new Dictionary<string, ExistingColumnInfo>(StringComparer.OrdinalIgnoreCase);
        await using var cmd = new NpgsqlCommand(sql, conn, tx);
        cmd.Parameters.AddWithValue("@schema", _schemaName);
        cmd.Parameters.AddWithValue("@table", tableName);

        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            var colName = reader.GetString(0);
            var dataType = reader.GetString(1);
            var isNullable = string.Equals(reader.GetString(2), "YES", StringComparison.OrdinalIgnoreCase);
            result[colName] = new ExistingColumnInfo(dataType, isNullable);
        }
        return result;
    }

    private static async Task ExecuteNonQueryAsync(
        NpgsqlConnection conn, 
        NpgsqlTransaction tx,
        string sql, 
        CancellationToken ct = default)
    {
        await using var cmd = new NpgsqlCommand(sql, conn, tx);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    private static string SanitizeIdentifier(string raw)
    {
        var cleaned = InvalidIndetifierRegex().Replace(raw.Trim().ToLowerInvariant(), "_");
        if(string.IsNullOrEmpty(cleaned) || char.IsDigit(cleaned[0]))
            cleaned = "_" + cleaned;
        return cleaned;
    }

    private static string Quote(string identifier) => $"\"{identifier}\"";

    [GeneratedRegex(@"[^a-z0-9_]")]
    private static partial Regex InvalidIndetifierRegex();

}