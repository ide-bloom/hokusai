using System.Diagnostics.CodeAnalysis;
using Hokusai.Core;
using Hokusai.Generate;
using Hokusai.Infer;
using Hokusai.Runtime;
using Hokusai.Verify;

namespace Hokusai.Engine;

public sealed record SchemaDrift(string Action, IReadOnlyList<string> ChangedTables);

/// <summary>The workflow behind every CLI command and Studio view.</summary>
public sealed class HokusaiEngine(Workspace workspace)
{
    public Workspace Workspace { get; } = workspace;

    public async Task<DataSourceConfig> RequireSourceAsync(CancellationToken ct) =>
        (await Workspace.LoadConfigAsync(ct).ConfigureAwait(false)).DataSource
        ?? throw new InvalidOperationException("No data source configured. Run 'hokusai connect' first.");

    /// <summary>Tests the connection and stores it (connection string encrypted with DPAPI).</summary>
    public async Task<SchemaInfo> ConnectAsync(DataSourceConfig source, CancellationToken ct)
    {
        await using var connection = await HkDb.OpenAsync(source.Provider, source.ConnectionString, ct).ConfigureAwait(false);
        var schema = await SchemaReader.ReadAsync(connection, source.Provider, ct).ConfigureAwait(false);
        source.Display = Redact(source.ConnectionString);
        var config = await Workspace.LoadConfigAsync(ct).ConfigureAwait(false);
        config.DataSource = source;
        await Workspace.SaveConfigAsync(config, ct).ConfigureAwait(false);
        return schema;
    }

    /// <summary>The connection string with passwords removed, safe to show and store in hokusai.json.</summary>
    public static string Redact(string connectionString)
    {
        var builder = new System.Data.Common.DbConnectionStringBuilder();
        try
        {
            builder.ConnectionString = connectionString;
        }
        catch (ArgumentException)
        {
            return "(unparseable connection string)";
        }

        foreach (var key in builder.Keys.Cast<string>().ToList())
        {
            if (key.Contains("password", StringComparison.OrdinalIgnoreCase) || string.Equals(key, "pwd", StringComparison.OrdinalIgnoreCase))
            {
                builder[key] = "***";
            }
        }

        return builder.ConnectionString;
    }

    public async Task<RecordingSession> StartRecordingAsync(string action, RecordTarget target, RecordOptions options, CancellationToken ct)
    {
        var source = await RequireSourceAsync(ct).ConfigureAwait(false);
        var config = await Workspace.LoadConfigAsync(ct).ConfigureAwait(false);
        var merged = new RecordOptions
        {
            UseOdbcAgent = options.UseOdbcAgent,
            PollInterval = options.PollInterval,
            FullSnapshotRowBudget = options.FullSnapshotRowBudget,
            WatchFolders = [.. options.WatchFolders, .. config.WatchFolders],
        };
        return await RecordingSession.StartAsync(Workspace, source, action, target, merged, ct).ConfigureAwait(false);
    }

    private static SqlDialect Dialect(DataSourceConfig source, System.Data.Common.DbConnection connection) => HkDb.DialectOf(source.Provider, connection);

    public async Task<ActionTemplate> InferAsync(string action, CancellationToken ct)
    {
        var source = await RequireSourceAsync(ct).ConfigureAwait(false);
        var config = await Workspace.LoadConfigAsync(ct).ConfigureAwait(false);
        var demos = await Workspace.LoadDemonstrationsAsync(action, ct).ConfigureAwait(false);
        SchemaInfo schema;
        SqlDialect dialect;
        await using (var connection = await HkDb.OpenAsync(source.Provider, source.ConnectionString, ct).ConfigureAwait(false))
        {
            schema = await SchemaReader.ReadAsync(connection, source.Provider, ct).ConfigureAwait(false);
            dialect = Dialect(source, connection);
        }

        foreach (var demo in demos.Where(NeedsResultRecovery))
        {
            await RecoverResultsAsync(source, demo, ct).ConfigureAwait(false);
            await Workspace.SaveDemonstrationAsync(demo, ct).ConfigureAwait(false);
        }

        var befores = new Dictionary<int, DatabaseSnapshot?>();
        foreach (var demo in demos)
        {
            befores[demo.Index] = await BeforeSnapshotAsync(source, schema, demo, ct).ConfigureAwait(false);
        }

        var template = TemplateInferrer.Infer(action, demos, new InferenceContext
        {
            Schema = schema,
            Dialect = dialect,
            Provider = demos.FirstOrDefault()?.Provider ?? source.Provider,
            Cultures = config.Cultures,
            BeforeSnapshot = d => befores.GetValueOrDefault(d.Index),
            Guards = await Workspace.LoadGuardsAsync(action, ct).ConfigureAwait(false),
        });
        template.Name = action;
        await Workspace.SaveTemplateAsync(template, ct).ConfigureAwait(false);
        return template;
    }

    /// <summary>Extended Events record statements but not result sets; those are recovered by replay.</summary>
    private static bool NeedsResultRecovery(Demonstration demo) =>
        demo.Provider == ProviderKind.SqlServer && demo.Baseline is not null && demo.Statements.Any(s => s.Result is null && s.Text.Contains("SELECT", StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// Replays the demonstration's recorded statements, in order, on a copy of its baseline inside a transaction
    /// that is rolled back, and keeps the result sets the app received.
    /// </summary>
    [SuppressMessage("Security", "CA2100:Review SQL queries for security vulnerabilities", Justification = "Replays the exact statements the app sent, on a throwaway copy.")]
    public async Task RecoverResultsAsync(DataSourceConfig source, Demonstration demo, CancellationToken ct)
    {
        var file = Path.Combine(Workspace.DemonstrationDirectory(demo.Action, demo.Index), demo.Baseline!);
        await using var copy = await DatabaseCopies.CreateCopyAsync(source, file, ct).ConfigureAwait(false);
        await using var connection = await HkDb.OpenAsync(ProviderKind.SqlServer, copy.ConnectionString, ct).ConfigureAwait(false);
        await using var transaction = await connection.BeginTransactionAsync(ct).ConfigureAwait(false);
        foreach (var statement in demo.Context.Concat(demo.Statements).OrderBy(s => s.Timestamp))
        {
            try
            {
                var result = await HkDb.ExecuteAsync(connection, transaction, ProviderKind.SqlServer, statement.Text, new Dictionary<string, object?>(), ct).ConfigureAwait(false);
                if (statement.Result is null && result.Columns.Count > 0 && demo.Statements.Contains(statement))
                {
                    statement.Result = ResultData.From(result);
                    statement.ResultRecovered = true;
                }
            }
            catch (Microsoft.Data.SqlClient.SqlException)
            {
                // Context statements may depend on session state (temp tables); demo statements still replay.
                if (demo.Statements.Contains(statement))
                {
                    break;
                }
            }
        }

        await transaction.RollbackAsync(ct).ConfigureAwait(false);
    }

    private async Task<DatabaseSnapshot?> BeforeSnapshotAsync(DataSourceConfig source, SchemaInfo schema, Demonstration demo, CancellationToken ct)
    {
        var dir = Workspace.DemonstrationDirectory(demo.Action, demo.Index);
        var saved = Path.Combine(dir, "before.json");
        if (File.Exists(saved))
        {
            return await HkJson.ReadAsync<DatabaseSnapshot>(saved, ct).ConfigureAwait(false);
        }

        if (demo.Baseline is null)
        {
            return null;
        }

        // Large database: snapshot the touched tables and the tables they reference from the baseline copy.
        var tables = demo.Tables.SelectMany(t => new[] { t }.Concat(schema.Table(t)?.ForeignKeys.Select(f => SqlNames.Bare(f.ReferencedTable)) ?? []))
            .Where(t => schema.Table(t) is not null).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        await using var copy = await DatabaseCopies.CreateCopyAsync(source, Path.Combine(dir, demo.Baseline), ct).ConfigureAwait(false);
        await using var connection = await HkDb.OpenAsync(source.Provider, copy.ConnectionString, ct).ConfigureAwait(false);
        return await StateDiffer.SnapshotAsync(connection, null, source.Provider, schema, tables, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Records the guard for a destructive statement that state-diff capture could not see the condition of, then
    /// re-infers so the template carries it. The action still has to pass verification to be enabled.
    /// </summary>
    public async Task<ActionTemplate> SetGuardAsync(string action, int step, string guard, CancellationToken ct)
    {
        var template = await Workspace.LoadTemplateAsync(action, ct).ConfigureAwait(false) ?? throw new InvalidOperationException($"No template for '{action}'. Run 'hokusai infer {action}' first.");
        var statement = template.AllStatements().FirstOrDefault(s => s.Id == step)
            ?? throw new InvalidOperationException($"'{action}' has no step {step}.");
        if (!statement.GuardRequired)
        {
            throw new InvalidOperationException($"Step {step} of '{action}' does not need a guard.");
        }

        guard = guard.Trim();
        if (guard.Length == 0 || guard.Contains(';', StringComparison.Ordinal) || guard.Contains("--", StringComparison.Ordinal) || guard.Contains("/*", StringComparison.Ordinal))
        {
            throw new InvalidOperationException("The guard must be a single SQL condition without ';' or comments, for example: Status = 'Draft'.");
        }

        var guards = await Workspace.LoadGuardsAsync(action, ct).ConfigureAwait(false);
        guards.ByShape[statement.Shape] = guard;
        await Workspace.SaveGuardsAsync(action, guards, ct).ConfigureAwait(false);
        return await InferAsync(action, ct).ConfigureAwait(false);
    }

    public async Task<VerificationReport> VerifyAsync(string action, string copyFrom, CancellationToken ct)
    {
        var source = await RequireSourceAsync(ct).ConfigureAwait(false);
        var template = await Workspace.LoadTemplateAsync(action, ct).ConfigureAwait(false) ?? throw new InvalidOperationException($"No template for '{action}'. Run 'hokusai infer {action}' first.");
        var demos = await Workspace.LoadDemonstrationsAsync(action, ct).ConfigureAwait(false);
        var report = await Verifier.VerifyAsync(template, demos, Workspace, source, copyFrom, ct).ConfigureAwait(false);
        await HkJson.WriteAsync(Workspace.VerificationPath(action), report, ct).ConfigureAwait(false);
        await Workspace.SaveTemplateAsync(template, ct).ConfigureAwait(false);
        return report;
    }

    public async Task<IReadOnlyList<string>> GenerateAsync(IReadOnlyList<string> actions, string output, bool api, bool mcp, bool tests, string name, CancellationToken ct)
    {
        var source = await RequireSourceAsync(ct).ConfigureAwait(false);
        var list = new List<GenerationAction>();
        foreach (var action in actions)
        {
            var template = await Workspace.LoadTemplateAsync(action, ct).ConfigureAwait(false) ?? throw new InvalidOperationException($"No template for '{action}'.");
            var report = await Workspace.LoadVerificationAsync(action, ct).ConfigureAwait(false);
            var demos = await Workspace.LoadDemonstrationsAsync(action, ct).ConfigureAwait(false);
            list.Add(new GenerationAction
            {
                Template = template,
                Report = report,
                Demonstrations = demos,
                BaselinePath = d => d.Baseline is null ? null : Path.Combine(Workspace.DemonstrationDirectory(d.Action, d.Index), d.Baseline),
            });
        }

        string? fileTemplate = null;
        if (source.Provider != ProviderKind.SqlServer && source.FilePath is not null)
        {
            fileTemplate = DatabaseCopies.FileTemplate(source);
        }

        return ProjectGenerator.Generate(list, new GenerationOptions { Name = name, OutputDirectory = output, Api = api, Mcp = mcp, Tests = tests, FileConnectionTemplate = fileTemplate });
    }

    public async Task<List<SchemaDrift>> SchemaCheckAsync(CancellationToken ct)
    {
        var source = await RequireSourceAsync(ct).ConfigureAwait(false);
        await using var connection = await HkDb.OpenAsync(source.Provider, source.ConnectionString, ct).ConfigureAwait(false);
        var schema = await SchemaReader.ReadAsync(connection, source.Provider, ct).ConfigureAwait(false);
        var drift = new List<SchemaDrift>();
        foreach (var action in Workspace.Actions())
        {
            var template = await Workspace.LoadTemplateAsync(action, ct).ConfigureAwait(false);
            if (template is null || schema.Fingerprint(template.Tables) == template.SchemaFingerprint)
            {
                continue;
            }

            var changed = template.Tables.Where(t => !template.TableFingerprints.TryGetValue(t, out var f) || f != schema.Fingerprint([t])).ToList();
            drift.Add(new SchemaDrift(template.Name, changed));
        }

        return drift;
    }
}
