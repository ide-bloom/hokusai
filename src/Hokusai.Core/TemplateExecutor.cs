using System.Data.Common;
using System.Globalization;
using Hokusai.Runtime;

namespace Hokusai.Core;

/// <summary>
/// Interprets a template against a connection. Verification uses this interpreter; generated projects contain
/// equivalent straight-line code built on the same <see cref="HkRun"/>, <see cref="HkEncode"/> and
/// <see cref="HkCondition"/> primitives.
/// </summary>
public static class TemplateExecutor
{
    private sealed class Scope(Scope? parent)
    {
        public Dictionary<int, HkResult?> Results { get; } = [];
        public Dictionary<string, object?>? Item { get; init; }

        public HkResult? Find(int step) => Results.TryGetValue(step, out var r) ? r : parent?.Find(step);

        public object? Value(string name, ActionInput input) =>
            Item is not null && Item.TryGetValue(name, out var v) ? v : parent is not null ? parent.Value(name, input) : input.Scalars.GetValueOrDefault(name);
    }

    public static async Task<ExecutionOutcome> ExecuteAsync(
        ActionTemplate template,
        DbConnection? connection,
        DbTransaction? transaction,
        ActionInput input,
        bool dryRun,
        CancellationToken ct)
    {
        if (!dryRun && connection is null)
        {
            throw new ArgumentNullException(nameof(connection));
        }

        var run = new HkRun(connection, transaction, template.Provider, dryRun);
        var root = new Scope(null);
        await RunStepsAsync(template, template.Steps, root, run, input, ct).ConfigureAwait(false);

        var outcome = new ExecutionOutcome { RowsAffected = run.RowsAffected };
        outcome.Statements.AddRange(run.Plan);
        if (!dryRun)
        {
            if (template.ResultStep is int rs)
            {
                outcome.Result = root.Find(rs);
            }

            if (template.FileOutput is { } file && root.Find(file.SourceStep) is { } source)
            {
                outcome.FileContent = CsvOutput.Write(file.Csv, source);
            }
        }

        return outcome;
    }

    private static async Task RunStepsAsync(ActionTemplate template, List<TemplateStep> steps, Scope scope, HkRun run, ActionInput input, CancellationToken ct)
    {
        foreach (var step in steps)
        {
            ct.ThrowIfCancellationRequested();
            switch (step)
            {
                case StatementStep s:
                    scope.Results[s.Id] = await run.ExecuteAsync(s.Id, s.Sql, [.. s.Bindings.Select(b => Argument(template, b, scope, input))], ct).ConfigureAwait(false);
                    break;
                case ConditionalStep c:
                    if (Evaluate(c.Condition, scope.Value(c.Condition.Parameter, input)))
                    {
                        await RunStepsAsync(template, c.Body, scope, run, input, ct).ConfigureAwait(false);
                    }

                    break;
                case LoopStep l:
                    foreach (var item in input.Collections.GetValueOrDefault(l.Collection) ?? [])
                    {
                        await RunStepsAsync(template, l.Body, new Scope(scope) { Item = item }, run, input, ct).ConfigureAwait(false);
                    }

                    break;
            }
        }
    }

    private static HkArg Argument(ActionTemplate template, Binding b, Scope scope, ActionInput input)
    {
        var name = b.Placeholder.TrimStart('@');
        switch (b.Kind)
        {
            case BindingKind.Input:
                var raw = scope.Value(b.Parameter!, input);
                if (template.Parameter(b.Parameter!)?.Lookup is { } lookup)
                {
                    return new HkLookupArg(name, lookup.Table, lookup.KeyColumn, lookup.DisplayColumn, raw);
                }

                return new HkValueArg(name, HkEncode.Encode(b.Encoding, raw));
            case BindingKind.Dependent:
                var step = b.SourceStep!.Value;
                return new HkDeferredArg(name, $"(result of step {step}: {b.SourceColumn})", () =>
                    HkEncode.Encode(b.Encoding, (scope.Find(step) ?? throw new HokusaiExecutionException($"Step {step} has not run.")).Value(0, b.SourceColumn!)));
            case BindingKind.Generated:
                return new HkValueArg(name, HkEncode.Encode(b.Encoding, HkGenerate.Value(b.Generator)));
            default:
                return new HkValueArg(name, ConstantValue(b));
        }
    }

    public static object? ConstantValue(Binding b) => b.ConstantType switch
    {
        "null" => null,
        "number" => decimal.Parse(b.Constant!, NumberStyles.Float, CultureInfo.InvariantCulture),
        _ => b.Constant,
    };

    public static bool Evaluate(Condition condition, object? value) => condition.Op switch
    {
        ConditionOp.IsTrue => HkCondition.IsTrue(value),
        ConditionOp.IsFalse => !HkCondition.IsTrue(value),
        ConditionOp.EqualsValue => HkCondition.EqualsValue(value, condition.Value),
        ConditionOp.NotEqualsValue => !HkCondition.EqualsValue(value, condition.Value),
        ConditionOp.IsEmpty => HkCondition.IsEmpty(value),
        ConditionOp.IsNotEmpty => !HkCondition.IsEmpty(value),
        _ => false,
    };
}
