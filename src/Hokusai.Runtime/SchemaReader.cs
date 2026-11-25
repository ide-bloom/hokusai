using System.Data;
using System.Data.Common;
using System.Data.OleDb;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Runtime.Versioning;

namespace Hokusai.Runtime;

/// <summary>Reads tables, columns, keys and foreign keys for every supported dialect.</summary>
public static class SchemaReader
{
    public static async Task<SchemaInfo> ReadAsync(DbConnection connection, ProviderKind provider, CancellationToken ct)
    {
        var dialect = HkDb.DialectOf(provider, connection);
        return dialect switch
        {
            SqlDialect.SqlServer => await ReadSqlServerAsync(connection, ct).ConfigureAwait(false),
            SqlDialect.Sqlite => await ReadSqliteAsync(connection, ct).ConfigureAwait(false),
            SqlDialect.Access when provider == ProviderKind.Access && OperatingSystem.IsWindows() => await ReadAccessAsync((OleDbConnection)connection, ct).ConfigureAwait(false),
            _ => await ReadGenericAsync(connection, ct).ConfigureAwait(false),
        };
    }

    private static string Text(object? value) => Convert.ToString(value, CultureInfo.InvariantCulture) ?? "";

    private static int Int(object? value) => Convert.ToInt32(value, CultureInfo.InvariantCulture);

    private static async Task<SchemaInfo> ReadSqlServerAsync(DbConnection connection, CancellationToken ct)
    {
        const string columnsSql = """
            SELECT s.name, t.name, c.name, ty.name, c.max_length, c.is_nullable, c.is_identity,
                   CASE WHEN c.default_object_id <> 0 THEN 1 ELSE 0 END, dc.definition, c.is_computed
            FROM sys.tables t
            JOIN sys.schemas s ON s.schema_id = t.schema_id
            JOIN sys.columns c ON c.object_id = t.object_id
            JOIN sys.types ty ON ty.user_type_id = c.user_type_id
            LEFT JOIN sys.default_constraints dc ON dc.object_id = c.default_object_id
            WHERE t.is_ms_shipped = 0
            ORDER BY s.name, t.name, c.column_id
            """;
        const string keysSql = """
            SELECT s.name, t.name, c.name
            FROM sys.indexes i
            JOIN sys.tables t ON t.object_id = i.object_id
            JOIN sys.schemas s ON s.schema_id = t.schema_id
            JOIN sys.index_columns ic ON ic.object_id = i.object_id AND ic.index_id = i.index_id
            JOIN sys.columns c ON c.object_id = ic.object_id AND c.column_id = ic.column_id
            WHERE i.is_primary_key = 1
            ORDER BY s.name, t.name, ic.key_ordinal
            """;
        const string fkSql = """
            SELECT ps.name, pt.name, pc.name, rs.name, rt.name, rc.name
            FROM sys.foreign_key_columns f
            JOIN sys.tables pt ON pt.object_id = f.parent_object_id
            JOIN sys.schemas ps ON ps.schema_id = pt.schema_id
            JOIN sys.columns pc ON pc.object_id = f.parent_object_id AND pc.column_id = f.parent_column_id
            JOIN sys.tables rt ON rt.object_id = f.referenced_object_id
            JOIN sys.schemas rs ON rs.schema_id = rt.schema_id
            JOIN sys.columns rc ON rc.object_id = f.referenced_object_id AND rc.column_id = f.referenced_column_id
            """;

        var tables = new Dictionary<string, TableSchema>(StringComparer.OrdinalIgnoreCase);
        TableSchema Get(object? schema, object? table)
        {
            var name = SqlNames.Bare(Text(schema) + "." + Text(table));
            if (!tables.TryGetValue(name, out var t))
            {
                t = new TableSchema { Name = name };
                tables[name] = t;
            }

            return t;
        }

        foreach (var r in await HkDb.QueryRowsAsync(connection, null, columnsSql, ct).ConfigureAwait(false))
        {
            var type = Text(r[3]);
            var maxBytes = Int(r[4]);
            int? max = type.ToUpperInvariant() switch
            {
                "NVARCHAR" or "NCHAR" => maxBytes < 0 ? null : maxBytes / 2,
                "VARCHAR" or "CHAR" or "VARBINARY" or "BINARY" => maxBytes < 0 ? null : maxBytes,
                _ => null,
            };
            Get(r[0], r[1]).Columns.Add(new ColumnSchema
            {
                Name = Text(r[2]),
                DataType = type,
                MaxLength = max,
                Nullable = Convert.ToBoolean(r[5], CultureInfo.InvariantCulture),
                IsIdentity = Convert.ToBoolean(r[6], CultureInfo.InvariantCulture) || Convert.ToBoolean(r[9], CultureInfo.InvariantCulture),
                HasDefault = Int(r[7]) == 1,
                DefaultExpression = r[8] is null ? null : Text(r[8]),
            });
        }

        foreach (var r in await HkDb.QueryRowsAsync(connection, null, keysSql, ct).ConfigureAwait(false))
        {
            Get(r[0], r[1]).PrimaryKey.Add(Text(r[2]));
        }

        foreach (var r in await HkDb.QueryRowsAsync(connection, null, fkSql, ct).ConfigureAwait(false))
        {
            Get(r[0], r[1]).ForeignKeys.Add(new ForeignKeySchema
            {
                Column = Text(r[2]),
                ReferencedTable = SqlNames.Bare(Text(r[3]) + "." + Text(r[4])),
                ReferencedColumn = Text(r[5]),
            });
        }

        return new SchemaInfo { Tables = [.. tables.Values.OrderBy(t => t.Name, StringComparer.OrdinalIgnoreCase)] };
    }

    private static async Task<SchemaInfo> ReadSqliteAsync(DbConnection connection, CancellationToken ct)
    {
        var info = new SchemaInfo();
        var names = await HkDb.QueryRowsAsync(connection, null, "SELECT name FROM sqlite_master WHERE type = 'table' AND name NOT LIKE 'sqlite_%' ORDER BY name", ct).ConfigureAwait(false);
        foreach (var n in names)
        {
            var name = Text(n[0]);
            var quoted = SqlNames.Quote(name, ProviderKind.Sqlite);
            var table = new TableSchema { Name = name };
            var pkOrder = new SortedDictionary<int, string>();
            foreach (var c in await HkDb.QueryRowsAsync(connection, null, $"PRAGMA table_info({quoted})", ct).ConfigureAwait(false))
            {
                var type = Text(c[2]);
                int? max = null;
                var open = type.IndexOf('(', StringComparison.Ordinal);
                if (open > 0 && int.TryParse(type[(open + 1)..].TrimEnd(')').Split(',')[0], CultureInfo.InvariantCulture, out var len))
                {
                    max = len;
                }

                table.Columns.Add(new ColumnSchema
                {
                    Name = Text(c[1]),
                    DataType = open > 0 ? type[..open].Trim() : type,
                    MaxLength = max,
                    Nullable = Int(c[3]) == 0 && Int(c[5]) == 0,
                    HasDefault = c[4] is not null,
                    DefaultExpression = c[4] is null ? null : Text(c[4]),
                });
                if (Int(c[5]) > 0)
                {
                    pkOrder[Int(c[5])] = Text(c[1]);
                }
            }

            table.PrimaryKey.AddRange(pkOrder.Values);
            if (table.PrimaryKey.Count == 1)
            {
                var pk = table.Column(table.PrimaryKey[0]);
                if (pk is not null && string.Equals(pk.DataType, "INTEGER", StringComparison.OrdinalIgnoreCase))
                {
                    // INTEGER PRIMARY KEY aliases the rowid, which SQLite assigns like an identity column.
                    pk.IsIdentity = true;
                }
            }

            foreach (var f in await HkDb.QueryRowsAsync(connection, null, $"PRAGMA foreign_key_list({quoted})", ct).ConfigureAwait(false))
            {
                table.ForeignKeys.Add(new ForeignKeySchema { ReferencedTable = Text(f[2]), Column = Text(f[3]), ReferencedColumn = Text(f[4]) });
            }

            info.Tables.Add(table);
        }

        return info;
    }

    [SupportedOSPlatform("windows")]
    [SuppressMessage("Security", "CA2100:Review SQL queries for security vulnerabilities", Justification = "Table names come from the provider's schema rowset.")]
    private static async Task<SchemaInfo> ReadAccessAsync(OleDbConnection connection, CancellationToken ct)
    {
        var info = new SchemaInfo();
        var tables = connection.GetOleDbSchemaTable(OleDbSchemaGuid.Tables, [null, null, null, "TABLE"]) ?? new DataTable();
        var columns = connection.GetOleDbSchemaTable(OleDbSchemaGuid.Columns, null) ?? new DataTable();
        var keys = connection.GetOleDbSchemaTable(OleDbSchemaGuid.Primary_Keys, null) ?? new DataTable();
        var fks = connection.GetOleDbSchemaTable(OleDbSchemaGuid.Foreign_Keys, null) ?? new DataTable();
        foreach (DataRow t in tables.Rows)
        {
            var name = Text(t["TABLE_NAME"]);
            var table = new TableSchema { Name = name };
            foreach (var c in columns.Rows.Cast<DataRow>()
                .Where(c => string.Equals(Text(c["TABLE_NAME"]), name, StringComparison.OrdinalIgnoreCase))
                .OrderBy(c => Int(c["ORDINAL_POSITION"])))
            {
                table.Columns.Add(new ColumnSchema
                {
                    Name = Text(c["COLUMN_NAME"]),
                    DataType = TypeMap.FromOleDbType(Int(c["DATA_TYPE"])),
                    MaxLength = c["CHARACTER_MAXIMUM_LENGTH"] is DBNull ? null : Int(c["CHARACTER_MAXIMUM_LENGTH"]),
                    Nullable = Convert.ToBoolean(c["IS_NULLABLE"], CultureInfo.InvariantCulture),
                    HasDefault = Convert.ToBoolean(c["COLUMN_HASDEFAULT"], CultureInfo.InvariantCulture),
                    DefaultExpression = c["COLUMN_DEFAULT"] is DBNull ? null : Text(c["COLUMN_DEFAULT"]),
                });
            }

            table.PrimaryKey.AddRange(keys.Rows.Cast<DataRow>()
                .Where(k => string.Equals(Text(k["TABLE_NAME"]), name, StringComparison.OrdinalIgnoreCase))
                .OrderBy(k => Int(k["ORDINAL"]))
                .Select(k => Text(k["COLUMN_NAME"])));
            foreach (var f in fks.Rows.Cast<DataRow>().Where(f => string.Equals(Text(f["FK_TABLE_NAME"]), name, StringComparison.OrdinalIgnoreCase)))
            {
                table.ForeignKeys.Add(new ForeignKeySchema { Column = Text(f["FK_COLUMN_NAME"]), ReferencedTable = Text(f["PK_TABLE_NAME"]), ReferencedColumn = Text(f["PK_COLUMN_NAME"]) });
            }

            // The column rowset does not flag AutoNumber; the key-info schema of an empty query does.
            await using var command = connection.CreateCommand();
            command.CommandText = $"SELECT * FROM {SqlNames.Quote(name, ProviderKind.Access)} WHERE 1 = 0";
            await using var reader = await command.ExecuteReaderAsync(CommandBehavior.KeyInfo | CommandBehavior.SchemaOnly, ct).ConfigureAwait(false);
            var schema = await reader.GetSchemaTableAsync(ct).ConfigureAwait(false);
            if (schema is not null)
            {
                foreach (DataRow s in schema.Rows)
                {
                    if (s["IsAutoIncrement"] is true && table.Column(Text(s["ColumnName"])) is { } col)
                    {
                        col.IsIdentity = true;
                        col.DataType = "COUNTER";
                    }
                }
            }

            info.Tables.Add(table);
        }

        return info;
    }

    private static async Task<SchemaInfo> ReadGenericAsync(DbConnection connection, CancellationToken ct)
    {
        var info = new SchemaInfo();
        var tables = await connection.GetSchemaAsync("Tables", ct).ConfigureAwait(false);
        var columns = await connection.GetSchemaAsync("Columns", ct).ConfigureAwait(false);
        foreach (DataRow t in tables.Rows)
        {
            if (tables.Columns.Contains("TABLE_TYPE") && !string.Equals(Text(t["TABLE_TYPE"]), "TABLE", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var name = Text(t["TABLE_NAME"]);
            var table = new TableSchema { Name = name };
            foreach (DataRow c in columns.Rows)
            {
                if (!string.Equals(Text(c["TABLE_NAME"]), name, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                table.Columns.Add(new ColumnSchema
                {
                    Name = Text(c["COLUMN_NAME"]),
                    DataType = columns.Columns.Contains("TYPE_NAME") ? Text(c["TYPE_NAME"]) : "TEXT",
                    MaxLength = columns.Columns.Contains("COLUMN_SIZE") && c["COLUMN_SIZE"] is not DBNull ? Int(c["COLUMN_SIZE"]) : null,
                    Nullable = !columns.Columns.Contains("NULLABLE") || c["NULLABLE"] is DBNull || Int(c["NULLABLE"]) != 0,
                });
            }

            info.Tables.Add(table);
        }

        return info;
    }
}
