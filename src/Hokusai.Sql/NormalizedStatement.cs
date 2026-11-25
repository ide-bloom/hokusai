using Hokusai.Runtime;

namespace Hokusai.Sql;

public enum LiteralKind
{
    Text,
    Number,
    Null,

    /// <summary>A bound parameter value (ODBC marker or sp_executesql argument).</summary>
    Bound,
}

/// <summary>One value that was replaced by a placeholder.</summary>
public sealed record SqlLiteral(int Index, string? Value, LiteralKind Kind, string? Column)
{
    public string Placeholder => HkDb.Placeholder(Index);
}

/// <summary>A value bound to a statement outside its text, in marker order.</summary>
public sealed record BoundValue(string Name, string? Value);

/// <summary>A statement with its values lifted out: the shape aligns demonstrations, the template replays them.</summary>
public sealed class NormalizedStatement
{
    public string Original { get; init; } = "";

    /// <summary>Canonical form: literals as ?, identifiers unquoted and upper-cased, single spaces.</summary>
    public string Shape { get; init; } = "";

    /// <summary>Original text with every literal replaced by a neutral placeholder.</summary>
    public string Template { get; init; } = "";
    public List<SqlLiteral> Literals { get; init; } = [];

    /// <summary>INSERT, UPDATE, DELETE, SELECT, EXEC, MERGE or OTHER, from the first statement of the batch.</summary>
    public string Kind { get; init; } = "OTHER";
    public string? Table { get; init; }
    public List<string> Tables { get; init; } = [];

    /// <summary>Columns assigned from server functions such as GETDATE() or NEWID(): generated values.</summary>
    public List<string> ServerGeneratedColumns { get; init; } = [];
    public List<string> Errors { get; init; } = [];

    public bool IsWrite => Kind is "INSERT" or "UPDATE" or "DELETE" or "MERGE" or "EXEC";
}

public interface ISqlNormalizer
{
    NormalizedStatement Normalize(string sql, IReadOnlyList<BoundValue> bound);
}

public static class SqlNormalizer
{
    public static ISqlNormalizer For(SqlDialect dialect) => dialect == SqlDialect.SqlServer ? new TSqlNormalizer() : new GenericNormalizer(dialect);

    /// <summary>Server functions whose value at execution time is a generated timestamp or identifier.</summary>
    public static bool IsGeneratorFunction(string name) => name.ToUpperInvariant() switch
    {
        "GETDATE" or "GETUTCDATE" or "SYSDATETIME" or "SYSUTCDATETIME" or "SYSDATETIMEOFFSET" or "CURRENT_TIMESTAMP"
            or "NEWID" or "NEWSEQUENTIALID" or "NOW" or "DATE" or "DATETIME" or "STRFTIME" or "JULIANDAY" or "CURRENT_DATE" or "CURRENT_TIME" => true,
        _ => false,
    };
}
