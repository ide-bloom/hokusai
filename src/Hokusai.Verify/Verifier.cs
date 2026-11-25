using Hokusai.Core;
using Hokusai.Runtime;
using Hokusai.Sql;

namespace Hokusai.Verify;

/// <summary>
/// Replays a template against a fresh copy of the database for each demonstration and compares the resulting
/// row changes, file content and result rows with what the app produced. Only an action whose every
/// demonstration matches is marked verified.
/// </summary>
public static class Verifier
{
    /// <param name="copySource">
    /// "recorded" to use the baseline captured at each demonstration's start mark, or a .bak/.mdf/.db/.accdb/.sql file
    /// to copy for every demonstration.
    /// </param>
    public static async Task<VerificationReport> VerifyAsync(
        ActionTemplate template,
        IReadOnlyList<Demonstration> demos,
        Workspace workspace,
        DataSourceConfig source,
        string copySource,
        CancellationToken ct)
    {
        var report = new VerificationReport { Action = template.Name, At = DateTimeOffset.Now, Source = copySource };
        report.Issues.AddRange(template.Issues);
        foreach (var demo in demos)
        {
            ct.ThrowIfCancellationRequested();
            var result = new DemoVerification { Index = demo.Index };
            report.Demonstrations.Add(result);
            try
            {
                var file = string.Equals(copySource, "recorded", StringComparison.OrdinalIgnoreCase)
                    ? demo.Baseline is null ? throw new InvalidOperationException("No baseline was captured at the start mark.") : Path.Combine(workspace.DemonstrationDirectory(demo.Action, demo.Index), demo.Baseline)
                    : copySource;
                await using var copy = await DatabaseCopies.CreateCopyAsync(source, file, ct).ConfigureAwait(false);
                await VerifyOnAsync(template, demo, copy.ConnectionString, result, ct).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                result.Error = Describe(ex);
                result.Matched = false;
            }
        }

        report.Verified = report.Issues.Count == 0 && report.Demonstrations.Count > 0 && report.Demonstrations.All(d => d.Matched);
        template.Status = report.Verified ? TemplateStatus.Verified : TemplateStatus.Unverified;
        return report;
    }

    /// <summary>Some providers (ODBC drivers in particular) throw exceptions with empty messages; name the type and look deeper.</summary>
    private static string Describe(Exception ex)
    {
        for (var e = ex; e is not null; e = e.InnerException)
        {
            var message = e is System.Data.Odbc.OdbcException odbc && odbc.Errors.Count > 0
                ? string.Join(" ", odbc.Errors.Cast<System.Data.Odbc.OdbcError>().Select(x => $"{x.SQLState} {x.Message}".Trim()))
                : e.Message;
            if (!string.IsNullOrWhiteSpace(message))
            {
                return ex.GetType().Name + ": " + message;
            }
        }

        return ex.GetType().FullName + " (no message) at " + ex.StackTrace?.Split('\n').FirstOrDefault()?.Trim();
    }

    /// <summary>Executes the template with the demonstration's inputs on <paramref name="connectionString"/> and fills <paramref name="result"/>.</summary>
    public static async Task VerifyOnAsync(ActionTemplate template, Demonstration demo, string connectionString, DemoVerification result, CancellationToken ct)
    {
        if (demo.StateDiff is null)
        {
            throw new InvalidOperationException("The demonstration has no recorded state diff to compare with.");
        }

        await using var connection = await HkDb.OpenAsync(template.Provider, connectionString, ct).ConfigureAwait(false);
        var schema = await SchemaReader.ReadAsync(connection, template.Provider, ct).ConfigureAwait(false);
        var tables = template.Tables.Concat(demo.StateDiff.Tables.Select(t => t.Table)).Distinct(StringComparer.OrdinalIgnoreCase).Where(t => schema.Table(t) is not null).ToList();
        var input = DemonstrationInputs.Build(template, demo);

        await using var transaction = await connection.BeginTransactionAsync(ct).ConfigureAwait(false);
        var before = await StateDiffer.SnapshotAsync(connection, transaction, template.Provider, schema, tables, ct).ConfigureAwait(false);
        var outcome = await TemplateExecutor.ExecuteAsync(template, connection, transaction, input, dryRun: false, ct).ConfigureAwait(false);
        var after = await StateDiffer.SnapshotAsync(connection, transaction, template.Provider, schema, tables, ct).ConfigureAwait(false);

        // The copy is discarded anyway; rolling back keeps a shared file-based copy reusable if cleanup is delayed.
        await transaction.RollbackAsync(ct).ConfigureAwait(false);

        result.Actual = StateDiffer.Diff(before, after);
        result.Mismatches = DiffComparer.Compare(demo.StateDiff, result.Actual, template.GeneratedColumns);
        CompareFiles(template, demo, outcome, result);
        CompareResult(template, demo, outcome, result);
        result.Matched = result.Mismatches.Count == 0 && result.FileMismatches.Count == 0;
    }

    private static void CompareFiles(ActionTemplate template, Demonstration demo, ExecutionOutcome outcome, DemoVerification result)
    {
        var expected = demo.FileEffects.LastOrDefault(f => f.Format == "csv" && f.After is not null);
        if (template.FileOutput is null)
        {
            if (demo.FileEffects.Count > 0)
            {
                result.FileMismatches.Add($"The app wrote {demo.FileEffects.Count} file(s); the template writes none.");
            }

            return;
        }

        if (expected is null)
        {
            result.FileMismatches.Add("The template writes a CSV file, but the app wrote none in this demonstration.");
            return;
        }

        var a = Normalize(expected.After!);
        var b = Normalize(outcome.FileContent ?? "");
        if (a == b)
        {
            return;
        }

        var expectedLines = a.Split('\n');
        var actualLines = b.Split('\n');
        for (var i = 0; i < Math.Max(expectedLines.Length, actualLines.Length); i++)
        {
            var e = i < expectedLines.Length ? expectedLines[i] : null;
            var x = i < actualLines.Length ? actualLines[i] : null;
            if (e != x)
            {
                result.FileMismatches.Add($"{Path.GetFileName(expected.Path)} line {i + 1}: expected {HkValue.Describe(e)}, got {HkValue.Describe(x)}");
            }
        }
    }

    private static string Normalize(string text) => text.Replace("\r\n", "\n", StringComparison.Ordinal);

    private static void CompareResult(ActionTemplate template, Demonstration demo, ExecutionOutcome outcome, DemoVerification result)
    {
        if (template.Kind != ActionKind.Read || template.ResultStep is null || outcome.Result is null)
        {
            return;
        }

        var shape = template.AllStatements().First(s => s.Id == template.ResultStep).Shape;
        var recorded = demo.Statements.LastOrDefault(s => s.Result is not null && ShapeOf(s, template, demo) == shape)?.Result;
        if (recorded is null)
        {
            return;
        }

        var actual = ResultData.From(outcome.Result);
        if (actual.Rows.Count != recorded.Rows.Count)
        {
            result.Mismatches.Add(new DiffMismatch("(result)", "Read", "rows", null, recorded.Rows.Count.ToString(System.Globalization.CultureInfo.InvariantCulture), actual.Rows.Count.ToString(System.Globalization.CultureInfo.InvariantCulture)));
            return;
        }

        for (var r = 0; r < actual.Rows.Count; r++)
        {
            for (var c = 0; c < Math.Min(actual.Columns.Count, recorded.Columns.Count); c++)
            {
                if (!HkValue.CanonEquals(recorded.Rows[r][c], actual.Rows[r][c]))
                {
                    result.Mismatches.Add(new DiffMismatch("(result)", "Read", "row " + (r + 1).ToString(System.Globalization.CultureInfo.InvariantCulture), recorded.Columns[c], recorded.Rows[r][c], actual.Rows[r][c]));
                }
            }
        }
    }

    private static string ShapeOf(StatementEvent e, ActionTemplate template, Demonstration demo)
    {
        var dialect = template.Provider switch
        {
            ProviderKind.SqlServer => SqlDialect.SqlServer,
            ProviderKind.Access => SqlDialect.Access,
            ProviderKind.Sqlite => SqlDialect.Sqlite,
            _ => demo.Provider == ProviderKind.SqlServer ? SqlDialect.SqlServer : SqlDialect.Generic,
        };
        return SqlNormalizer.For(dialect).Normalize(e.Text, [.. e.Parameters.Select(p => new BoundValue(p.Name, p.Value))]).Shape;
    }
}
