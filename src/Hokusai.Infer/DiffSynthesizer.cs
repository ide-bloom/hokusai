using System.Globalization;
using Hokusai.Core;
using Hokusai.Runtime;

namespace Hokusai.Infer;

/// <summary>
/// For file databases captured only through state diffs (Access, SQLite without the ODBC agent), rebuilds the
/// statements that produce the observed row changes: inserts in foreign-key order with identity reads, key-based
/// updates and deletes. The synthesized statements then go through the same alignment and correlation.
/// </summary>
public static class DiffSynthesizer
{
    public static List<StatementEvent> Synthesize(Demonstration demo, SchemaInfo schema, SqlDialect dialect)
    {
        var events = new List<StatementEvent>();
        if (demo.StateDiff is null)
        {
            return events;
        }

        var time = UiState.CommitTime(demo);
        var order = TopologicalOrder(schema, demo.StateDiff.Tables.Select(t => t.Table));
        string Q(string name) => HkDb.QuoteIdentifier(name, dialect);

        void Add(string text, List<string?> values, ResultData? result = null)
        {
            time = time.AddTicks(1);
            events.Add(new StatementEvent
            {
                Source = "diff",
                Timestamp = time,
                Text = text,
                Parameters = [.. values.Select((v, i) => new StatementParameter { Name = (i + 1).ToString(CultureInfo.InvariantCulture), Value = v })],
                Result = result,
                RowCount = result is null ? 1 : null,
            });
        }

        // Updates of rows that existed before come first: apps read and bump counters before inserting the rows
        // that use them. A numeric change gets the read the app must have done, so the new value can be explained.
        foreach (var td in demo.StateDiff.Tables.OrderBy(t => t.Table, StringComparer.OrdinalIgnoreCase))
        {
            foreach (var change in td.Changes.Where(c => c.Kind == ChangeKind.Update).OrderBy(c => SortKey(c.Key)))
            {
                var changed = change.ChangedColumns().ToList();
                var keys = td.KeyColumns;
                var where = string.Join(" AND ", keys.Select(k => Q(k) + " = ?"));
                var keyValues = keys.Select(k => change.Key.GetValueOrDefault(k)).ToList();
                var numeric = changed.Where(c => IsNumber(change.Before!.GetValueOrDefault(c)) && IsNumber(change.After![c])).ToList();
                if (numeric.Count > 0)
                {
                    Add($"SELECT {string.Join(", ", numeric.Select(Q))} FROM {Q(td.Table)} WHERE {where}", keyValues,
                        new ResultData { Columns = numeric, Rows = [[.. numeric.Select(c => change.Before!.GetValueOrDefault(c))]] });
                }

                Add($"UPDATE {Q(td.Table)} SET {string.Join(", ", changed.Select(c => Q(c) + " = ?"))} WHERE {where}", [.. changed.Select(c => change.After![c]), .. keyValues]);
            }
        }

        foreach (var name in order)
        {
            var td = demo.StateDiff.Tables.First(t => string.Equals(t.Table, name, StringComparison.OrdinalIgnoreCase));
            var table = schema.Table(name);
            var identity = table?.Columns.FirstOrDefault(c => c.IsIdentity);
            foreach (var change in td.Changes.Where(c => c.Kind == ChangeKind.Insert).OrderBy(c => SortKey(c.Key)))
            {
                var row = change.After!;
                var cols = row.Keys.Where(k => identity is null || !string.Equals(k, identity.Name, StringComparison.OrdinalIgnoreCase)).ToList();
                Add($"INSERT INTO {Q(name)} ({string.Join(", ", cols.Select(Q))}) VALUES ({string.Join(", ", cols.Select(_ => "?"))})", [.. cols.Select(c => row[c])]);
                if (identity is not null)
                {
                    var read = dialect switch
                    {
                        SqlDialect.Sqlite => $"SELECT last_insert_rowid() AS {Q(identity.Name)}",
                        SqlDialect.SqlServer => $"SELECT CAST(SCOPE_IDENTITY() AS bigint) AS {Q(identity.Name)}",
                        _ => $"SELECT @@IDENTITY AS {Q(identity.Name)}",
                    };
                    Add(read, [], new ResultData { Columns = [identity.Name], Rows = [[row.GetValueOrDefault(identity.Name)]] });
                }
            }
        }

        // Deletes: children before parents. Child rows removed together with their parent are deleted through the
        // foreign key, as the app did; their own keys would not be known to it.
        var deletedRows = demo.StateDiff.Tables.ToDictionary(
            t => t.Table,
            t => t.Changes.Where(c => c.Kind == ChangeKind.Delete).Select(c => c.Before!).ToList(),
            StringComparer.OrdinalIgnoreCase);
        foreach (var name in Enumerable.Reverse(order))
        {
            var td = demo.StateDiff.Tables.First(t => string.Equals(t.Table, name, StringComparison.OrdinalIgnoreCase));
            var deletes = td.Changes.Where(c => c.Kind == ChangeKind.Delete).OrderBy(c => SortKey(c.Key)).ToList();
            if (deletes.Count == 0)
            {
                continue;
            }

            var viaParent = schema.Table(name)?.ForeignKeys.FirstOrDefault(fk =>
                !string.Equals(SqlNames.Bare(fk.ReferencedTable), name, StringComparison.OrdinalIgnoreCase) &&
                deletedRows.TryGetValue(SqlNames.Bare(fk.ReferencedTable), out var parents) && parents.Count > 0 &&
                deletes.All(d => parents.Any(p => HkValue.CanonEquals(p.GetValueOrDefault(fk.ReferencedColumn), d.Before!.GetValueOrDefault(fk.Column)))));
            if (viaParent is not null && !td.KeyColumns.SequenceEqual([viaParent.Column], StringComparer.OrdinalIgnoreCase))
            {
                foreach (var value in deletes.Select(d => d.Before!.GetValueOrDefault(viaParent.Column)).Distinct())
                {
                    Add($"DELETE FROM {Q(name)} WHERE {Q(viaParent.Column)} = ?", [value]);
                }

                continue;
            }

            foreach (var change in deletes)
            {
                var keys = td.KeyedByHash ? [.. change.Before!.Keys] : td.KeyColumns;
                var source = td.KeyedByHash ? change.Before! : change.Key;
                Add($"DELETE FROM {Q(name)} WHERE {string.Join(" AND ", keys.Select(k => Q(k) + " = ?"))}", [.. keys.Select(k => source.GetValueOrDefault(k))]);
            }
        }

        return events;
    }

    private static bool IsNumber(string? value) => value is not null && decimal.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out _);

    private static string SortKey(Dictionary<string, string?> key) =>
        string.Join("|", key.Values.Select(v => v is not null && decimal.TryParse(v, NumberStyles.Float, CultureInfo.InvariantCulture, out var d) ? d.ToString("0000000000000000.####", CultureInfo.InvariantCulture) : v ?? ""));

    /// <summary>Referenced tables before referencing ones, so parent rows exist when children are inserted.</summary>
    internal static List<string> TopologicalOrder(SchemaInfo schema, IEnumerable<string> tables)
    {
        var set = tables.Distinct(StringComparer.OrdinalIgnoreCase).Order(StringComparer.OrdinalIgnoreCase).ToList();
        var result = new List<string>();
        var visiting = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        void Visit(string t)
        {
            if (result.Contains(t, StringComparer.OrdinalIgnoreCase) || !visiting.Add(t))
            {
                return;
            }

            foreach (var fk in schema.Table(t)?.ForeignKeys ?? [])
            {
                var parent = set.FirstOrDefault(s => string.Equals(s, SqlNames.Bare(fk.ReferencedTable), StringComparison.OrdinalIgnoreCase));
                if (parent is not null && !string.Equals(parent, t, StringComparison.OrdinalIgnoreCase))
                {
                    Visit(parent);
                }
            }

            result.Add(t);
        }

        foreach (var t in set)
        {
            Visit(t);
        }

        return result;
    }
}
