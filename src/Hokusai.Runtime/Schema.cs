using System.Security.Cryptography;
using System.Text;

namespace Hokusai.Runtime;

public sealed class ColumnSchema
{
    public string Name { get; set; } = "";
    public string DataType { get; set; } = "";
    public int? MaxLength { get; set; }
    public bool Nullable { get; set; } = true;
    public bool IsIdentity { get; set; }
    public bool HasDefault { get; set; }
    public string? DefaultExpression { get; set; }
}

public sealed class ForeignKeySchema
{
    public string Column { get; set; } = "";
    public string ReferencedTable { get; set; } = "";
    public string ReferencedColumn { get; set; } = "";
}

public sealed class TableSchema
{
    public string Name { get; set; } = "";
    public List<ColumnSchema> Columns { get; set; } = [];
    public List<string> PrimaryKey { get; set; } = [];
    public List<ForeignKeySchema> ForeignKeys { get; set; } = [];

    public ColumnSchema? Column(string name) =>
        Columns.FirstOrDefault(c => string.Equals(c.Name, name, StringComparison.OrdinalIgnoreCase));
}

public sealed class SchemaInfo
{
    public List<TableSchema> Tables { get; set; } = [];

    public TableSchema? Table(string name)
    {
        var bare = SqlNames.Bare(name);
        return Tables.FirstOrDefault(t => string.Equals(t.Name, bare, StringComparison.OrdinalIgnoreCase))
            ?? Tables.FirstOrDefault(t => string.Equals(SqlNames.Bare(t.Name), bare, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// Fingerprint over the given tables only, so drift in tables an action never touches does not block it.
    /// Column order is ignored because statements address columns by name.
    /// </summary>
    public string Fingerprint(IEnumerable<string> tables)
    {
        var sb = new StringBuilder();
        foreach (var name in tables.Select(SqlNames.Bare).Distinct(StringComparer.OrdinalIgnoreCase).Order(StringComparer.OrdinalIgnoreCase))
        {
            var t = Table(name);
            sb.Append("T:").Append(name.ToUpperInvariant());
            if (t is null)
            {
                sb.Append(":missing;");
                continue;
            }

            foreach (var c in t.Columns.OrderBy(c => c.Name, StringComparer.OrdinalIgnoreCase))
            {
                sb.Append("|C:").Append(c.Name.ToUpperInvariant()).Append(':').Append(c.DataType.ToUpperInvariant())
                  .Append(':').Append(c.MaxLength?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "-")
                  .Append(':').Append(c.Nullable ? 'N' : 'R');
            }

            sb.Append("|PK:").AppendJoin(',', t.PrimaryKey.Select(k => k.ToUpperInvariant()));
            foreach (var fk in t.ForeignKeys.OrderBy(f => f.Column, StringComparer.OrdinalIgnoreCase))
            {
                sb.Append("|FK:").Append(fk.Column.ToUpperInvariant()).Append("->").Append(SqlNames.Bare(fk.ReferencedTable).ToUpperInvariant())
                  .Append('.').Append(fk.ReferencedColumn.ToUpperInvariant());
            }

            sb.Append(';');
        }

        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(sb.ToString())))[..32];
    }
}

/// <summary>Maps database type names from all supported providers onto the CLR types used for parameters.</summary>
public static class TypeMap
{
    public static string ToClr(string dataType)
    {
        var t = dataType.ToUpperInvariant();
        var paren = t.IndexOf('(', StringComparison.Ordinal);
        if (paren > 0)
        {
            t = t[..paren].Trim();
        }

        return t switch
        {
            "INT" or "SMALLINT" or "TINYINT" or "INTEGER" or "MEDIUMINT" or "SHORT" or "BYTE" or "COUNTER" or "AUTOINCREMENT" => "int",
            "BIGINT" or "LONG" or "INT8" => "long",
            "BIT" or "BOOLEAN" or "BOOL" or "YESNO" => "bool",
            "DECIMAL" or "NUMERIC" or "MONEY" or "SMALLMONEY" or "CURRENCY" => "decimal",
            "FLOAT" or "REAL" or "DOUBLE" or "SINGLE" or "DOUBLE PRECISION" => "double",
            "DATE" => "DateOnly",
            "DATETIME" or "DATETIME2" or "SMALLDATETIME" or "TIMESTAMP" or "DATETIMEOFFSET" => "DateTime",
            "UNIQUEIDENTIFIER" or "GUID" => "Guid",
            _ => "string",
        };
    }

    /// <summary>OLE DB type codes reported by the ACE provider's schema rowsets.</summary>
    public static string FromOleDbType(int code) => code switch
    {
        2 => "SMALLINT",
        3 => "INTEGER",
        4 => "REAL",
        5 => "DOUBLE",
        6 => "CURRENCY",
        7 => "DATETIME",
        11 => "BIT",
        17 => "BYTE",
        20 => "BIGINT",
        72 => "GUID",
        128 => "BINARY",
        130 => "TEXT",
        131 => "DECIMAL",
        _ => "TEXT",
    };
}

/// <summary>Identifier helpers shared by the parser, snapshotter and generated code.</summary>
public static class SqlNames
{
    /// <summary>Strips quoting and the default <c>dbo</c> schema so <c>[dbo].[Customer]</c> and <c>Customer</c> compare equal.</summary>
    public static string Bare(string name)
    {
        var parts = name.Split('.').Select(Unquote).ToList();
        if (parts.Count > 1 && string.Equals(parts[^2], "dbo", StringComparison.OrdinalIgnoreCase))
        {
            return parts[^1];
        }

        return string.Join('.', parts.TakeLast(2));
    }

    public static string Unquote(string part)
    {
        part = part.Trim();
        if (part.Length >= 2 && ((part[0] == '[' && part[^1] == ']') || (part[0] == '"' && part[^1] == '"') || (part[0] == '`' && part[^1] == '`')))
        {
            return part[1..^1];
        }

        return part;
    }

    public static string Quote(string name, ProviderKind provider)
    {
        var parts = name.Split('.').Select(Unquote);
        return provider switch
        {
            ProviderKind.SqlServer or ProviderKind.Access => string.Join('.', parts.Select(p => "[" + p.Replace("]", "]]", StringComparison.Ordinal) + "]")),
            _ => string.Join('.', parts.Select(p => "\"" + p.Replace("\"", "\"\"", StringComparison.Ordinal) + "\"")),
        };
    }
}
