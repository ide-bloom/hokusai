using System.Data.Common;

namespace Hokusai.Runtime;

public enum ChangeKind
{
    Insert,
    Update,
    Delete,
}

/// <summary>All rows of one table at one moment, as canonical strings.</summary>
public sealed class TableSnapshot
{
    public string Table { get; set; } = "";
    public List<string> Columns { get; set; } = [];
    public List<string> KeyColumns { get; set; } = [];
    public List<Dictionary<string, string?>> Rows { get; set; } = [];
}

public sealed class DatabaseSnapshot
{
    public List<TableSnapshot> Tables { get; set; } = [];

    public TableSnapshot? Table(string name) =>
        Tables.FirstOrDefault(t => string.Equals(SqlNames.Bare(t.Table), SqlNames.Bare(name), StringComparison.OrdinalIgnoreCase));
}

public sealed class RowChange
{
    public ChangeKind Kind { get; set; }

    /// <summary>Primary key values, or the row hash under the key "#hash" for tables without a key.</summary>
    public Dictionary<string, string?> Key { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    public Dictionary<string, string?>? Before { get; set; }
    public Dictionary<string, string?>? After { get; set; }

    public IEnumerable<string> ChangedColumns()
    {
        if (Before is null || After is null)
        {
            return (After ?? Before ?? []).Keys;
        }

        return After.Keys.Where(k => !HkValue.CanonEquals(Before.GetValueOrDefault(k), After[k]));
    }
}

public sealed class TableDiff
{
    public string Table { get; set; } = "";
    public List<string> KeyColumns { get; set; } = [];
    public bool KeyedByHash { get; set; }
    public List<RowChange> Changes { get; set; } = [];
}

public sealed class DatabaseDiff
{
    public List<TableDiff> Tables { get; set; } = [];

    public bool IsEmpty => Tables.All(t => t.Changes.Count == 0);

    public int ChangeCount => Tables.Sum(t => t.Changes.Count);
}

/// <summary>One row or column where a replay differed from what the app did.</summary>
public sealed record DiffMismatch(string Table, string Kind, string Key, string? Column, string? Expected, string? Actual)
{
    public override string ToString() =>
        Column is null
            ? $"{Table} [{Key}]: {Kind} expected {Expected ?? "nothing"}, got {Actual ?? "nothing"}"
            : $"{Table} [{Key}].{Column}: expected {HkValue.Describe(Expected)}, got {HkValue.Describe(Actual)} ({Kind})";
}

/// <summary>Takes snapshots of tables and computes row-level differences between two snapshots.</summary>
public static class StateDiffer
{
    public static async Task<DatabaseSnapshot> SnapshotAsync(
        DbConnection connection,
        DbTransaction? transaction,
        ProviderKind provider,
        SchemaInfo schema,
        IEnumerable<string> tables,
        CancellationToken ct)
    {
        var dialect = HkDb.DialectOf(provider, connection);
        var snapshot = new DatabaseSnapshot();
        foreach (var name in tables.Select(SqlNames.Bare).Distinct(StringComparer.OrdinalIgnoreCase))
        {
            var table = schema.Table(name) ?? throw new HokusaiExecutionException($"Table '{name}' does not exist.");

            // Row versions change on every write and carry no business data, so they are left out of snapshots.
            var columns = table.Columns
                .Where(c => !string.Equals(c.DataType, "timestamp", StringComparison.OrdinalIgnoreCase) && !string.Equals(c.DataType, "rowversion", StringComparison.OrdinalIgnoreCase))
                .Select(c => c.Name)
                .ToList();
            var select = $"SELECT {string.Join(", ", columns.Select(c => HkDb.QuoteIdentifier(c, dialect)))} FROM {HkDb.QuoteIdentifier(table.Name, dialect)}";
            var ts = new TableSnapshot { Table = table.Name, Columns = columns, KeyColumns = [.. table.PrimaryKey] };
            foreach (var row in await HkDb.QueryRowsAsync(connection, transaction, select, ct).ConfigureAwait(false))
            {
                var dict = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
                for (var i = 0; i < columns.Count; i++)
                {
                    dict[columns[i]] = HkValue.Canon(row[i]);
                }

                ts.Rows.Add(dict);
            }

            snapshot.Tables.Add(ts);
        }

        return snapshot;
    }

    public static DatabaseDiff Diff(DatabaseSnapshot before, DatabaseSnapshot after)
    {
        var diff = new DatabaseDiff();
        var names = before.Tables.Select(t => t.Table).Union(after.Tables.Select(t => t.Table), StringComparer.OrdinalIgnoreCase);
        foreach (var name in names)
        {
            var b = before.Table(name) ?? new TableSnapshot { Table = name };
            var a = after.Table(name) ?? new TableSnapshot { Table = name, KeyColumns = b.KeyColumns };
            var keys = a.KeyColumns.Count > 0 ? a.KeyColumns : b.KeyColumns;
            var td = new TableDiff { Table = name, KeyColumns = [.. keys], KeyedByHash = keys.Count == 0 };
            td.Changes.AddRange(keys.Count > 0 ? DiffKeyed(b.Rows, a.Rows, keys) : DiffByHash(b.Rows, a.Rows));
            if (td.Changes.Count > 0)
            {
                diff.Tables.Add(td);
            }
        }

        return diff;
    }

    private static string KeyText(Dictionary<string, string?> row, IReadOnlyList<string> keys) =>
        string.Join("\u001F", keys.Select(k => row.GetValueOrDefault(k) ?? "␀"));

    private static IEnumerable<RowChange> DiffKeyed(List<Dictionary<string, string?>> before, List<Dictionary<string, string?>> after, IReadOnlyList<string> keys)
    {
        var b = new Dictionary<string, Dictionary<string, string?>>(StringComparer.Ordinal);
        foreach (var row in before)
        {
            b[KeyText(row, keys)] = row;
        }

        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var row in after)
        {
            var k = KeyText(row, keys);
            seen.Add(k);
            var key = keys.ToDictionary(c => c, c => row.GetValueOrDefault(c), StringComparer.OrdinalIgnoreCase);
            if (!b.TryGetValue(k, out var old))
            {
                yield return new RowChange { Kind = ChangeKind.Insert, Key = key, After = row };
            }
            else if (row.Any(p => !HkValue.CanonEquals(old.GetValueOrDefault(p.Key), p.Value)))
            {
                yield return new RowChange { Kind = ChangeKind.Update, Key = key, Before = old, After = row };
            }
        }

        foreach (var (k, row) in b)
        {
            if (!seen.Contains(k))
            {
                yield return new RowChange { Kind = ChangeKind.Delete, Key = keys.ToDictionary(c => c, c => row.GetValueOrDefault(c), StringComparer.OrdinalIgnoreCase), Before = row };
            }
        }
    }

    private static IEnumerable<RowChange> DiffByHash(List<Dictionary<string, string?>> before, List<Dictionary<string, string?>> after)
    {
        // Without a key, rows are a multiset: an update shows up as one delete plus one insert.
        var remaining = before.GroupBy(r => HkValue.RowHash(r)).ToDictionary(g => g.Key, g => new Queue<Dictionary<string, string?>>(g), StringComparer.Ordinal);
        foreach (var row in after)
        {
            var h = HkValue.RowHash(row);
            if (remaining.TryGetValue(h, out var q) && q.Count > 0)
            {
                q.Dequeue();
                continue;
            }

            yield return new RowChange { Kind = ChangeKind.Insert, Key = new(StringComparer.OrdinalIgnoreCase) { ["#hash"] = h }, After = row };
        }

        foreach (var (h, q) in remaining)
        {
            foreach (var row in q)
            {
                yield return new RowChange { Kind = ChangeKind.Delete, Key = new(StringComparer.OrdinalIgnoreCase) { ["#hash"] = h }, Before = row };
            }
        }
    }
}

/// <summary>
/// Compares the diff a replay produced with the diff the app produced. Columns holding generated values
/// (timestamps, GUIDs) are excluded; when such a column is part of the key, rows are matched on content instead.
/// </summary>
public static class DiffComparer
{
    public static List<DiffMismatch> Compare(DatabaseDiff expected, DatabaseDiff actual, IReadOnlyCollection<string> ignoredColumns)
    {
        var mismatches = new List<DiffMismatch>();
        var names = expected.Tables.Select(t => t.Table).Union(actual.Tables.Select(t => t.Table), StringComparer.OrdinalIgnoreCase);
        foreach (var name in names)
        {
            var e = expected.Tables.FirstOrDefault(t => string.Equals(t.Table, name, StringComparison.OrdinalIgnoreCase)) ?? new TableDiff { Table = name };
            var a = actual.Tables.FirstOrDefault(t => string.Equals(t.Table, name, StringComparison.OrdinalIgnoreCase)) ?? new TableDiff { Table = name };
            var ignored = new HashSet<string>(
                ignoredColumns.Where(c => c.StartsWith(SqlNames.Bare(name) + ".", StringComparison.OrdinalIgnoreCase)).Select(c => c[(SqlNames.Bare(name).Length + 1)..]),
                StringComparer.OrdinalIgnoreCase);
            var keys = (e.KeyColumns.Count > 0 ? e.KeyColumns : a.KeyColumns).Where(k => !ignored.Contains(k)).ToList();
            var keyed = keys.Count > 0 && keys.Count == (e.KeyColumns.Count > 0 ? e.KeyColumns : a.KeyColumns).Count;
            CompareTable(name, e.Changes, a.Changes, keyed ? keys : null, ignored, mismatches);
        }

        return mismatches;
    }

    private static Dictionary<string, string?> Row(RowChange c) => c.After ?? c.Before ?? [];

    private static string Signature(RowChange c, HashSet<string> ignored) =>
        c.Kind + ":" + string.Join("\u001F", Row(c).Where(p => !ignored.Contains(p.Key)).OrderBy(p => p.Key, StringComparer.OrdinalIgnoreCase).Select(p => p.Key.ToUpperInvariant() + "=" + Normalize(p.Value)));

    private static string Normalize(string? v) =>
        v is not null && decimal.TryParse(v, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var d) ? HkValue.CanonDecimal(d) : v ?? "␀";

    private static void CompareTable(string table, List<RowChange> expected, List<RowChange> actual, List<string>? keys, HashSet<string> ignored, List<DiffMismatch> mismatches)
    {
        if (keys is null)
        {
            var pool = actual.GroupBy(c => Signature(c, ignored)).ToDictionary(g => g.Key, g => g.Count(), StringComparer.Ordinal);
            var unmatchedActual = new List<RowChange>(actual);
            foreach (var change in expected)
            {
                var sig = Signature(change, ignored);
                if (pool.TryGetValue(sig, out var n) && n > 0)
                {
                    pool[sig] = n - 1;
                    unmatchedActual.Remove(unmatchedActual.First(c => Signature(c, ignored) == sig));
                    continue;
                }

                mismatches.Add(new DiffMismatch(table, change.Kind.ToString(), Describe(Row(change), ignored), null, Describe(Row(change), ignored), null));
            }

            foreach (var change in unmatchedActual)
            {
                mismatches.Add(new DiffMismatch(table, change.Kind.ToString(), Describe(Row(change), ignored), null, null, Describe(Row(change), ignored)));
            }

            return;
        }

        string KeyOf(RowChange c) => string.Join(", ", keys.Select(k => k + "=" + (Row(c).GetValueOrDefault(k) ?? "NULL")));
        var actualByKey = actual.GroupBy(KeyOf).ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);
        var matched = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var change in expected)
        {
            var key = KeyOf(change);
            if (!actualByKey.TryGetValue(key, out var other))
            {
                mismatches.Add(new DiffMismatch(table, change.Kind.ToString(), key, null, change.Kind.ToString(), null));
                continue;
            }

            matched.Add(key);
            if (other.Kind != change.Kind)
            {
                mismatches.Add(new DiffMismatch(table, change.Kind.ToString(), key, null, change.Kind.ToString(), other.Kind.ToString()));
                continue;
            }

            var er = Row(change);
            var ar = Row(other);
            foreach (var column in er.Keys.Union(ar.Keys, StringComparer.OrdinalIgnoreCase))
            {
                if (ignored.Contains(column))
                {
                    continue;
                }

                var ev = er.GetValueOrDefault(column);
                var av = ar.GetValueOrDefault(column);
                if (!HkValue.CanonEquals(ev, av))
                {
                    mismatches.Add(new DiffMismatch(table, change.Kind.ToString(), key, column, ev, av));
                }
            }
        }

        foreach (var change in actual)
        {
            if (!matched.Contains(KeyOf(change)))
            {
                mismatches.Add(new DiffMismatch(table, change.Kind.ToString(), KeyOf(change), null, null, change.Kind.ToString()));
            }
        }
    }

    private static string Describe(Dictionary<string, string?> row, HashSet<string> ignored) =>
        "{" + string.Join(", ", row.Where(p => !ignored.Contains(p.Key)).Select(p => p.Key + "=" + HkValue.Describe(p.Value))) + "}";
}
