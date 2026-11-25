using System.Data.Common;
using System.Globalization;

namespace Hokusai.Runtime;

/// <summary>Invalid input detected while executing: a lookup that matches no row (or several), or a missing foreign key.</summary>
public sealed class HkInputException : Exception
{
    public HkInputException()
    {
    }

    public HkInputException(string message)
        : base(message)
    {
    }

    public HkInputException(string message, Exception inner)
        : base(message, inner)
    {
    }
}

/// <summary>One value bound to a placeholder.</summary>
public abstract record HkArg(string Name);

/// <summary>A value known before execution (input, constant or generated).</summary>
public sealed record HkValueArg(string Name, object? Value) : HkArg(Name);

/// <summary>A value read from an earlier statement's result; unknown during a dry run.</summary>
public sealed record HkDeferredArg(string Name, string Display, Func<object?> Value) : HkArg(Name);

/// <summary>A display value the app's combo box turned into a key; resolved with a parameterized query.</summary>
public sealed record HkLookupArg(string Name, string Table, string KeyColumn, string DisplayColumn, object? Display) : HkArg(Name);

public sealed class HkPlannedStatement
{
    public int Step { get; set; }
    public string Sql { get; set; } = "";
    public Dictionary<string, string?> Arguments { get; set; } = [];
}

/// <summary>
/// Runs the statements of one action in order. In a dry run nothing is executed (not even lookups); the planned
/// statements are recorded with placeholders described instead.
/// </summary>
public sealed class HkRun(DbConnection? connection, DbTransaction? transaction, ProviderKind provider, bool dryRun)
{
    public bool DryRun { get; } = dryRun;
    public ProviderKind Provider { get; } = provider;
    public List<HkPlannedStatement> Plan { get; } = [];
    public long RowsAffected { get; private set; }

    public async Task<HkResult?> ExecuteAsync(int step, string sql, IReadOnlyList<HkArg> args, CancellationToken ct)
    {
        var planned = new HkPlannedStatement { Step = step, Sql = sql };
        var values = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
        foreach (var arg in args)
        {
            switch (arg)
            {
                case HkValueArg v:
                    values[v.Name] = v.Value;
                    planned.Arguments[v.Name] = HkValue.Canon(v.Value);
                    break;
                case HkDeferredArg d when DryRun:
                    planned.Arguments[d.Name] = d.Display;
                    break;
                case HkDeferredArg d:
                    values[d.Name] = d.Value();
                    planned.Arguments[d.Name] = HkValue.Canon(values[d.Name]);
                    break;
                case HkLookupArg l when DryRun || l.Display is null:
                    values[l.Name] = null;
                    planned.Arguments[l.Name] = l.Display is null ? null : $"(key of {l.Table} where {l.DisplayColumn} = {HkValue.Describe(HkValue.Canon(l.Display))})";
                    break;
                case HkLookupArg l:
                    values[l.Name] = await HkLookup.ResolveAsync(connection!, transaction, Provider, l.Table, l.KeyColumn, l.DisplayColumn, l.Display!, ct).ConfigureAwait(false);
                    planned.Arguments[l.Name] = HkValue.Canon(values[l.Name]);
                    break;
            }
        }

        Plan.Add(planned);
        if (DryRun)
        {
            return null;
        }

        var result = await HkDb.ExecuteAsync(connection!, transaction, Provider, sql, values, ct).ConfigureAwait(false);
        RowsAffected += Math.Max(0, result.RowsAffected);
        return result;
    }
}

public static class HkLookup
{
    /// <summary>Maps a display value to its key the way the app's combo box did, requiring exactly one match.</summary>
    public static async Task<object?> ResolveAsync(DbConnection connection, DbTransaction? transaction, ProviderKind provider, string table, string keyColumn, string displayColumn, object display, CancellationToken ct)
    {
        var dialect = HkDb.DialectOf(provider, connection);
        var sql = $"SELECT {HkDb.QuoteIdentifier(keyColumn, dialect)} FROM {HkDb.QuoteIdentifier(table, dialect)} WHERE {HkDb.QuoteIdentifier(displayColumn, dialect)} = {HkDb.Placeholder(0)}";
        var result = await HkDb.ExecuteAsync(connection, transaction, provider, sql, new Dictionary<string, object?> { ["hk0"] = display }, ct).ConfigureAwait(false);
        return result.Rows.Count switch
        {
            1 => result.Rows[0][0],
            0 => throw new HkInputException($"No {table} has {displayColumn} = '{display}'."),
            _ => throw new HkInputException($"{result.Rows.Count} rows in {table} have {displayColumn} = '{display}'; the value is ambiguous."),
        };
    }

    /// <summary>Checks that a foreign-key value exists before any statement runs.</summary>
    public static async Task EnsureExistsAsync(DbConnection connection, DbTransaction? transaction, ProviderKind provider, string table, string column, object? value, string parameter, CancellationToken ct)
    {
        if (value is null)
        {
            return;
        }

        var dialect = HkDb.DialectOf(provider, connection);
        var sql = $"SELECT COUNT(*) FROM {HkDb.QuoteIdentifier(table, dialect)} WHERE {HkDb.QuoteIdentifier(column, dialect)} = {HkDb.Placeholder(0)}";
        var result = await HkDb.ExecuteAsync(connection, transaction, provider, sql, new Dictionary<string, object?> { ["hk0"] = value }, ct).ConfigureAwait(false);
        if (Convert.ToInt64(result.Rows[0][0], CultureInfo.InvariantCulture) == 0)
        {
            throw new HkInputException($"'{parameter}': no {table} with {column} = '{value}'.");
        }
    }
}

/// <summary>Condition primitives shared by the interpreter and generated code.</summary>
public static class HkCondition
{
    public static bool IsTrue(object? value) => value is true || HkValue.Canon(value) == "1";

    public static bool IsEmpty(object? value) => string.IsNullOrEmpty(HkValue.Canon(value));

    public static bool EqualsValue(object? value, string? expected) => HkValue.CanonEquals(HkValue.Canon(value), expected);
}

/// <summary>Fresh generated values: timestamps and GUIDs.</summary>
public static class HkGenerate
{
    public static object Value(string? generator) => generator switch
    {
        "utcnow" => DateTime.UtcNow,
        "today" => DateTime.Today,
        "guid" => Guid.NewGuid(),
        _ => DateTime.Now,
    };
}
