using System.Globalization;
using Hokusai.Core;
using Hokusai.Runtime;

namespace Hokusai.Infer;

/// <summary>
/// Explains a CSV file the app wrote as a formatting of a query result: delimiter, header, quoting, column
/// mapping and per-column number or date formats.
/// </summary>
public static class FileOutputInference
{
    private static readonly string[] Delimiters = [",", ";", "\t", "|"];

    private static readonly string?[] Formats =
    [
        null, "n:0.00", "n:0.0", "n:0", "n:0.##", "n:#,##0.00", "d:yyyy-MM-dd", "d:dd.MM.yyyy", "d:MM/dd/yyyy", "d:dd/MM/yyyy",
        "d:yyyy-MM-dd HH:mm:ss", "d:yyyy-MM-ddTHH:mm:ss",
    ];

    private sealed record Layout(string Delimiter, string NewLine, bool Trailing, bool QuoteAll, List<List<string>> Rows);

    private static Layout? Detect(string content)
    {
        var newline = content.Contains("\r\n", StringComparison.Ordinal) ? "\r\n" : "\n";
        var trailing = content.EndsWith('\n');
        var lines = content.Split(newline).Where(l => l.Length > 0).ToList();
        foreach (var d in Delimiters)
        {
            var rows = CsvOutput.Parse(content, d);
            if (rows.Count > 0 && rows.All(r => r.Count == rows[0].Count) && (rows[0].Count > 1 || Delimiters.All(o => !content.Contains(o, StringComparison.Ordinal))))
            {
                var quoteAll = lines.Count > 0 && lines.All(l => l.StartsWith('"'));
                return new Layout(d, newline, trailing, quoteAll, rows);
            }
        }

        return null;
    }

    private static string Format(string? value, string? format)
    {
        try
        {
            return CsvOutput.FormatValue(value, format);
        }
        catch (FormatException)
        {
            return "\u0000";
        }
    }

    /// <summary>Returns the CSV spec that reproduces <paramref name="content"/> from <paramref name="result"/>, or null.</summary>
    public static CsvSpec? Match(string content, ResultData result)
    {
        var layout = Detect(content);
        if (layout is null)
        {
            return null;
        }

        bool header;
        if (layout.Rows.Count == result.Rows.Count + 1)
        {
            header = true;
        }
        else if (layout.Rows.Count == result.Rows.Count)
        {
            header = false;
        }
        else
        {
            return null;
        }

        var data = header ? layout.Rows.Skip(1).ToList() : layout.Rows;
        var spec = new CsvSpec
        {
            Delimiter = layout.Delimiter,
            Header = header,
            HeaderNames = header ? [.. layout.Rows[0]] : [],
            NewLine = layout.NewLine,
            TrailingNewLine = layout.Trailing,
            QuoteAll = layout.QuoteAll,
        };
        var width = layout.Rows[0].Count;
        for (var c = 0; c < width; c++)
        {
            var headerName = header ? layout.Rows[0][c] : null;
            var candidates = Enumerable.Range(0, result.Columns.Count)
                .OrderByDescending(r => string.Equals(result.Columns[r], headerName, StringComparison.OrdinalIgnoreCase));
            CsvColumn? found = null;
            foreach (var r in candidates)
            {
                foreach (var f in Formats)
                {
                    if (data.Select((row, i) => (row, i)).All(x => Format(result.Rows[x.i][r], f) == x.row[c]))
                    {
                        found = new CsvColumn { SourceColumn = result.Columns[r], Format = f };
                        break;
                    }
                }

                if (found is not null)
                {
                    break;
                }
            }

            if (found is null)
            {
                return null;
            }

            spec.Columns.Add(found);
        }

        return spec;
    }

    /// <summary>
    /// Without captured statements, finds a table whose rows, projected and ordered, produce the CSV, and returns
    /// the equivalent SELECT with its result. This lets state-diff-only sources (Access) still infer exports.
    /// </summary>
    public static StatementEvent? SynthesizeExportQuery(string content, DatabaseSnapshot before, SqlDialect dialect, DateTimeOffset time)
    {
        var layout = Detect(content);
        if (layout is null)
        {
            return null;
        }

        foreach (var header in new[] { true, false })
        {
            var data = header ? layout.Rows.Skip(1).ToList() : layout.Rows;
            foreach (var table in before.Tables.Where(t => t.Rows.Count == data.Count && data.Count > 0))
            {
                var mapping = new List<(string Column, string? Format)>();
                for (var c = 0; c < layout.Rows[0].Count; c++)
                {
                    var cells = data.Select(r => r[c]).Order(StringComparer.Ordinal).ToList();
                    var headerName = header ? layout.Rows[0][c] : null;
                    var hit = table.Columns
                        .OrderByDescending(col => string.Equals(col, headerName, StringComparison.OrdinalIgnoreCase))
                        .SelectMany(col => Formats.Select(f => (col, f)))
                        .FirstOrDefault(x => table.Rows.Select(r => Format(r.GetValueOrDefault(x.col), x.f)).Order(StringComparer.Ordinal).SequenceEqual(cells));
                    if (hit.col is null)
                    {
                        mapping.Clear();
                        break;
                    }

                    mapping.Add(hit);
                }

                if (mapping.Count == 0)
                {
                    continue;
                }

                var orderColumns = table.KeyColumns.Concat(table.Columns).Distinct(StringComparer.OrdinalIgnoreCase);
                foreach (var orderBy in orderColumns)
                {
                    foreach (var descending in new[] { false, true })
                    {
                        var sorted = descending
                            ? table.Rows.OrderByDescending(r => r.GetValueOrDefault(orderBy), ValueComparer.Instance).ToList()
                            : table.Rows.OrderBy(r => r.GetValueOrDefault(orderBy), ValueComparer.Instance).ToList();
                        if (!sorted.Select((r, i) => (r, i)).All(x => mapping.Select((m, c) => Format(x.r.GetValueOrDefault(m.Column), m.Format) == data[x.i][c]).All(b => b)))
                        {
                            continue;
                        }

                        var columns = mapping.Select(m => m.Column).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
                        string Q(string n) => HkDb.QuoteIdentifier(n, dialect);
                        return new StatementEvent
                        {
                            Source = "diff",
                            Timestamp = time,
                            Text = $"SELECT {string.Join(", ", columns.Select(Q))} FROM {Q(table.Table)} ORDER BY {Q(orderBy)}{(descending ? " DESC" : "")}",
                            Result = new ResultData { Columns = columns, Rows = [.. sorted.Select(r => columns.Select(c => r.GetValueOrDefault(c)).ToList())] },
                        };
                    }
                }
            }
        }

        return null;
    }

    private sealed class ValueComparer : IComparer<string?>
    {
        public static ValueComparer Instance { get; } = new();

        public int Compare(string? x, string? y)
        {
            if (x is null || y is null)
            {
                return x is null ? (y is null ? 0 : -1) : 1;
            }

            if (decimal.TryParse(x, NumberStyles.Float, CultureInfo.InvariantCulture, out var a) && decimal.TryParse(y, NumberStyles.Float, CultureInfo.InvariantCulture, out var b))
            {
                return a.CompareTo(b);
            }

            return StringComparer.OrdinalIgnoreCase.Compare(x, y);
        }
    }
}
