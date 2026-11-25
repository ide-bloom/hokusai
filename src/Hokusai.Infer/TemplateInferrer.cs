using System.Globalization;
using Hokusai.Core;
using Hokusai.Runtime;
using Hokusai.Sql;

namespace Hokusai.Infer;

public sealed class InferenceContext
{
    public SchemaInfo Schema { get; init; } = new();
    public SqlDialect Dialect { get; init; } = SqlDialect.SqlServer;
    public ProviderKind Provider { get; init; } = ProviderKind.SqlServer;
    public IReadOnlyList<string> Cultures { get; init; } = ["en-US", "de-DE"];

    /// <summary>All tables as they were at a demonstration's start mark; used for lookups and state-diff exports.</summary>
    public Func<Demonstration, DatabaseSnapshot?> BeforeSnapshot { get; init; } = _ => null;

    /// <summary>Guards the user supplied for destructive statements whose condition could not be observed.</summary>
    public Guards Guards { get; init; } = new();
}

/// <summary>Turns the demonstrations of one action into an <see cref="ActionTemplate"/> with evidence for every decision.</summary>
public sealed class TemplateInferrer
{
    private static readonly HashSet<string> DataKinds = new(StringComparer.Ordinal) { "INSERT", "UPDATE", "DELETE", "SELECT", "EXEC", "MERGE" };

    private readonly InferenceContext _ctx;
    private readonly IReadOnlyList<Demonstration> _demos;
    private readonly ActionTemplate _template;
    private readonly List<List<DemoStatement>> _prepared = [];
    private readonly Dictionary<(int Demo, int Index), (Slot Slot, int Iteration)> _owner = [];
    private readonly List<Slot> _slots = [];
    private readonly HashSet<string> _usedNames = new(StringComparer.Ordinal);
    private readonly Dictionary<string, TemplateParameter> _paramsByKey = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<Dictionary<string, ControlValue>> _values = [];
    private readonly List<Dictionary<string, (UiEvent Source, GridData Grid)>> _grids = [];
    private int _nextStepId = 1;

    private sealed record DemoStatement(int Demo, int Index, StatementEvent Event, NormalizedStatement N);

    private sealed class Slot
    {
        public required StatementStep Step { get; init; }
        public Dictionary<int, List<DemoStatement>> Instances { get; } = [];
        public string? Collection { get; set; }
        public Slot? Loop { get; set; }
    }

    private TemplateInferrer(string action, IReadOnlyList<Demonstration> demos, InferenceContext ctx)
    {
        _ctx = ctx;
        _demos = demos;
        _template = new ActionTemplate { Name = action, Provider = ctx.Provider, DemonstrationCount = demos.Count };
    }

    public static ActionTemplate Infer(string action, IReadOnlyList<Demonstration> demos, InferenceContext ctx)
    {
        var inferrer = new TemplateInferrer(action, demos, ctx);
        inferrer.Run();
        return inferrer._template;
    }

    private void Evidence(string subject, string rule, string detail, double confidence) =>
        _template.Evidence.Add(new Evidence { Subject = subject, Rule = rule, Detail = detail, Confidence = confidence });

    private void Run()
    {
        if (_demos.Count == 0)
        {
            _template.Issues.Add("No demonstrations recorded.");
            return;
        }

        if (_demos.Count == 1)
        {
            _template.Issues.Add("Only one demonstration: inputs cannot be told apart from constants. Record at least two with different values.");
        }

        Prepare();
        if (_prepared.All(p => p.Count == 0))
        {
            _template.Issues.Add("No data effect was observed in any demonstration (no statements, row changes or file writes). Logic without a data effect, such as printing, is out of scope.");
            return;
        }

        foreach (var demo in _demos)
        {
            var at = UiState.CommitTime(demo);
            _values.Add(UiState.ValuesAt(demo, at));
            _grids.Add(UiState.GridsAt(demo, at));
        }

        var nodes = Aligner.Align([.. _prepared.Select(p => (IReadOnlyList<string>)[.. p.Select(s => s.N.Shape)])]);
        _template.Steps.AddRange(BuildSteps(nodes));

        foreach (var slot in _slots)
        {
            Correlate(slot);
        }

        InferFileOutput();
        Finish();
    }

    private void Prepare()
    {
        for (var d = 0; d < _demos.Count; d++)
        {
            var demo = _demos[d];
            var events = demo.Statements.Count > 0 ? demo.Statements : DiffSynthesizer.Synthesize(demo, _ctx.Schema, _ctx.Dialect);
            if (demo.Statements.Count == 0)
            {
                foreach (var effect in demo.FileEffects.Where(f => f.Format == "csv" && f.After is not null))
                {
                    var before = _ctx.BeforeSnapshot(demo);
                    if (before is not null && FileOutputInference.SynthesizeExportQuery(effect.After!, before, _ctx.Dialect, effect.Timestamp) is { } query)
                    {
                        events = [.. events, query];
                    }
                }
            }

            var list = new List<DemoStatement>();
            foreach (var e in events.OrderBy(e => e.Timestamp))
            {
                var normalizer = e.Source == "diff" ? new GenericNormalizer(_ctx.Dialect) : SqlNormalizer.For(_ctx.Dialect);
                var n = normalizer.Normalize(e.Text, [.. e.Parameters.Select(p => new BoundValue(p.Name, p.Value))]);
                if (!DataKinds.Contains(n.Kind))
                {
                    continue;
                }

                if (n.Errors.Count > 0)
                {
                    Evidence($"demo {demo.Index}", "parse", $"Statement parsed with the generic tokenizer: {n.Errors[0]}", 0.5);
                }

                list.Add(new DemoStatement(d, list.Count, e, n));
            }

            _prepared.Add(list);
        }
    }

    private List<TemplateStep> BuildSteps(List<AlignedNode> nodes)
    {
        var steps = new List<TemplateStep>();
        var all = Enumerable.Range(0, _demos.Count).ToHashSet();
        var i = 0;
        while (i < nodes.Count)
        {
            var node = nodes[i];
            if (node is AlignedLoop loop)
            {
                steps.AddRange(BuildLoop(loop));
                i++;
                continue;
            }

            var presence = node.Presence.ToHashSet();
            if (presence.SetEquals(all))
            {
                steps.Add(BuildStatement((AlignedStatement)node).Step);
                i++;
                continue;
            }

            // Consecutive statements present in the same demonstrations share one condition.
            var group = new List<AlignedStatement>();
            while (i < nodes.Count && nodes[i] is AlignedStatement s && s.Presence.ToHashSet().SetEquals(presence))
            {
                group.Add(s);
                i++;
            }

            var conditional = new ConditionalStep();
            conditional.Body.AddRange(group.Select(g => BuildStatement(g).Step));
            conditional.Condition = InferCondition(presence, conditional.Body.OfType<StatementStep>().First().Id);
            steps.Add(conditional);
        }

        return steps;
    }

    private Slot BuildStatement(AlignedStatement node, Slot? loop = null, string? collection = null)
    {
        var first = node.Instances.First();
        var sample = _prepared[first.Key][first.Value];
        var slot = new Slot
        {
            Step = new StatementStep { Id = _nextStepId++, Sql = sample.N.Template, Shape = sample.N.Shape, Kind = sample.N.Kind, Table = sample.N.Table },
            Loop = loop,
            Collection = collection,
        };
        foreach (var (d, index) in node.Instances)
        {
            var ds = _prepared[d][index];
            slot.Instances[d] = [ds];
            _owner[(d, index)] = (slot, 0);
        }

        // A delete rebuilt from a state diff shows which rows went, not the condition the app applied to choose them.
        if (sample.N.Kind == "DELETE" && node.Instances.All(p => _prepared[p.Key][p.Value].Event.Source == "diff"))
        {
            slot.Step.GuardRequired = true;
        }

        slot.Step.Trigger = node.Instances
            .Select(p => UiState.Preceding(_demos[p.Key], _prepared[p.Key][p.Value].Event.Timestamp)?.Key)
            .OfType<string>()
            .GroupBy(k => k)
            .OrderByDescending(g => g.Count())
            .FirstOrDefault()?.Key;
        if (sample.Event.Result is { } r)
        {
            slot.Step.ResultColumns = [.. r.Columns];
        }

        _slots.Add(slot);
        return slot;
    }

    private List<TemplateStep> BuildLoop(AlignedLoop loop)
    {
        var counts = Enumerable.Range(0, _demos.Count).Select(d => loop.Iterations.TryGetValue(d, out var it) ? it.Count : 0).ToList();
        string? gridKey = null;
        foreach (var key in _grids.SelectMany(g => g.Keys).Distinct(StringComparer.OrdinalIgnoreCase))
        {
            var rows = _grids.Select(g => g.TryGetValue(key, out var x) ? x.Grid.Rows.Count(r => r.Any(c => !string.IsNullOrEmpty(c))) : 0).ToList();
            if (rows.SequenceEqual(counts))
            {
                gridKey = key;
                break;
            }
        }

        if (gridKey is null && counts.Distinct().Count() == 1 && counts[0] > 0)
        {
            // Same count everywhere and no grid to tie it to: the "loop" is just repeated statements.
            Evidence("loop", "unroll", $"Block of {loop.Body.Count} statement(s) repeats {counts[0]} times in every demonstration; treated as fixed statements.", 0.8);
            var unrolled = new List<TemplateStep>();
            for (var it = 0; it < counts[0]; it++)
            {
                for (var b = 0; b < loop.Body.Count; b++)
                {
                    var instances = loop.Iterations.ToDictionary(p => p.Key, p => p.Value[it][b]);
                    unrolled.Add(BuildStatement(new AlignedStatement(loop.Body[b], instances)).Step);
                }
            }

            return unrolled;
        }

        var collection = new InputList { Name = "lines" };
        if (gridKey is null)
        {
            _template.Issues.Add($"A block of {loop.Body.Count} statement(s) repeats {string.Join("/", counts)} times, but no grid had that many rows in each demonstration.");
        }
        else
        {
            var source = _grids.Select(g => g.TryGetValue(gridKey, out var x) ? x.Source : null).First(s => s is not null)!;
            collection.Name = Naming.Unique(Naming.Identifier(source.Label, gridKey, "lines"), _usedNames);
            collection.AutomationId = gridKey;
            collection.Label = source.Label ?? source.Name;
            _template.Collections.Add(collection);
            Evidence($"collection {collection.Name}", "loop-count", $"Loop iterations ({string.Join(", ", counts)}) equal the non-empty rows of grid '{gridKey}' in each demonstration.", 0.95);
        }

        var step = new LoopStep { Collection = collection.Name };
        var loopSlot = new Slot { Step = new StatementStep { Id = 0 }, Collection = collection.Name };
        for (var b = 0; b < loop.Body.Count; b++)
        {
            var bodyIndex = b;
            var firstDemo = loop.Iterations.First();
            var slot = BuildStatement(new AlignedStatement(loop.Body[b], new Dictionary<int, int> { [firstDemo.Key] = firstDemo.Value[0][bodyIndex] }), loopSlot, collection.Name);
            slot.Instances.Clear();
            foreach (var (d, iterations) in loop.Iterations)
            {
                slot.Instances[d] = [.. iterations.Select(it => _prepared[d][it[bodyIndex]])];
                for (var it = 0; it < iterations.Count; it++)
                {
                    _owner[(d, iterations[it][bodyIndex])] = (slot, it);
                }
            }

            step.Body.Add(slot.Step);
        }

        return [step];
    }

    private sealed record Observation(int Demo, int Iteration, DemoStatement Statement, string? Value, LiteralKind Kind, string? Column);

    private void Correlate(Slot slot)
    {
        var literalCount = slot.Instances.Values.First()[0].N.Literals.Count;
        for (var j = 0; j < literalCount; j++)
        {
            var obs = slot.Instances
                .SelectMany(p => p.Value.Select((ds, it) => new Observation(p.Key, it, ds, ds.N.Literals[j].Value, ds.N.Literals[j].Kind, ds.N.Literals[j].Column)))
                .ToList();
            var column = obs.Select(o => o.Column).FirstOrDefault(c => c is not null);
            var target = column is null || slot.Step.Table is null ? null : slot.Step.Table + "." + column;
            var binding = Classify(slot, obs, target);
            binding.Placeholder = HkDb.Placeholder(j);
            binding.TargetColumn = target;
            slot.Step.Bindings.Add(binding);
        }
    }

    private ColumnSchema? TargetColumn(string? target)
    {
        if (target is null)
        {
            return null;
        }

        var dot = target.LastIndexOf('.');
        return _ctx.Schema.Table(target[..dot])?.Column(target[(dot + 1)..]);
    }

    private Binding Classify(Slot slot, List<Observation> obs, string? target)
    {
        var subject = $"step {slot.Step.Id} {HkDb.Placeholder(slot.Step.Bindings.Count)}{(target is null ? "" : " (" + target + ")")}";
        var distinctDemos = obs.Select(o => o.Demo).Distinct().Count();
        var allEqual = obs.All(o => HkValue.CanonEquals(o.Value, obs[0].Value));

        if (allEqual && (distinctDemos >= 2 || obs.Count >= 2))
        {
            // Identity values often coincide when every demonstration starts from the same baseline; a value read
            // earlier in the same demonstration explains them better than a constant would.
            if (FindDependent(slot, obs, subject) is { } sameDependent)
            {
                return sameDependent;
            }

            Evidence(subject, "constant", $"Value {HkValue.Describe(obs[0].Value)} is the same in all {obs.Count} occurrence(s).", 0.9);
            return Constant(obs[0]);
        }

        if (FindInput(slot, obs, target, subject) is { } input)
        {
            return input;
        }

        if (FindDependent(slot, obs, subject) is { } dependent)
        {
            return dependent;
        }

        if (FindLookup(slot, obs, target, subject) is { } lookup)
        {
            return lookup;
        }

        var generated = obs.Select(o => ValueCorrelator.Generated(o.Value, _demos[o.Demo])).ToList();
        if (generated.All(g => g is not null) && generated.Select(g => g!.Value.Generator + g.Value.Encoding).Distinct().Count() == 1)
        {
            var g = generated[0]!.Value;
            Evidence(subject, "generated:" + g.Generator, $"Values {string.Join(", ", obs.Select(o => HkValue.Describe(o.Value)))} are fresh {g.Generator} values at demonstration time.", 0.85);
            if (target is not null)
            {
                _template.GeneratedColumns.Add(target);
            }

            return new Binding { Kind = BindingKind.Generated, Generator = g.Generator, Encoding = g.Encoding, Confidence = 0.85 };
        }

        if (obs.Count == 1)
        {
            Evidence(subject, "constant", $"Value {HkValue.Describe(obs[0].Value)} occurs in one demonstration only and matches no input; assumed constant.", 0.4);
            return Constant(obs[0], 0.4);
        }

        _template.Issues.Add($"Step {slot.Step.Id}: value {string.Join(" / ", obs.Select(o => HkValue.Describe(o.Value)))}{(target is null ? "" : " for " + target)} varies across demonstrations and matches no input, earlier result or generator.");
        return Constant(obs[0], 0);
    }

    private static Binding Constant(Observation o, double confidence = 0.9) => new()
    {
        Kind = BindingKind.Constant,
        Constant = o.Value,
        ConstantType = o.Value is null ? "null" : o.Kind == LiteralKind.Number ? "number" : "string",
        Confidence = confidence,
    };

    private IReadOnlyList<string> Cultures(int demo) =>
        [.. new[] { _demos[demo].Culture }.OfType<string>().Concat(_ctx.Cultures).Distinct(StringComparer.OrdinalIgnoreCase)];

    private static List<List<string?>> NonEmptyRows(GridData grid) => [.. grid.Rows.Where(r => r.Any(c => !string.IsNullOrEmpty(c)))];

    private Binding? FindInput(Slot slot, List<Observation> obs, string? target, string subject)
    {
        Dictionary<string, (Transform T, string Key, string? Column, int Order)>? candidates = null;
        foreach (var o in obs)
        {
            var set = new Dictionary<string, (Transform, string, string?, int)>(StringComparer.Ordinal);
            if (slot.Collection is not null && _template.Collections.FirstOrDefault(c => c.Name == slot.Collection) is { AutomationId: { } gridKey }
                && _grids[o.Demo].TryGetValue(gridKey, out var g))
            {
                var rows = NonEmptyRows(g.Grid);
                if (o.Iteration < rows.Count)
                {
                    for (var c = 0; c < g.Grid.Columns.Count; c++)
                    {
                        var cell = c < rows[o.Iteration].Count ? rows[o.Iteration][c] : null;
                        foreach (var t in ValueCorrelator.Transforms(cell, o.Value, UiEventKind.ValueChanged, Cultures(o.Demo)))
                        {
                            set.TryAdd($"grid:{g.Grid.Columns[c]}:{t.Signature}", (t, gridKey, g.Grid.Columns[c], -1));
                        }
                    }
                }
            }

            var order = 0;
            foreach (var cv in _values[o.Demo].Values)
            {
                foreach (var t in ValueCorrelator.Transforms(cv.Value, o.Value, cv.Kind, Cultures(o.Demo)))
                {
                    set.TryAdd($"ctl:{cv.Key}:{t.Signature}", (t, cv.Key, null, order));
                }

                order++;
            }

            if (candidates is null)
            {
                candidates = set;
            }
            else
            {
                foreach (var k in candidates.Keys.Where(k => !set.ContainsKey(k)).ToList())
                {
                    candidates.Remove(k);
                }
            }

            if (candidates.Count == 0)
            {
                return null;
            }
        }

        if (candidates is null || candidates.Count == 0)
        {
            return null;
        }

        var best = candidates.Values.OrderBy(c => c.Column is null ? 1 : 0).ThenBy(c => c.T.Rank).ThenBy(c => c.Order).First();
        var alternatives = candidates.Values.Select(c => c.Key + (c.Column is null ? "" : "/" + c.Column)).Distinct().Count() - 1;
        var confidence = alternatives > 0 ? 0.7 : 0.95;
        var transform = Refine(best.T, obs, target);
        var parameter = GetOrCreateParameter(best.Key, best.Column, slot.Collection, transform, target, obs);
        if (transform.EmptyAsNull)
        {
            parameter.EmptyAsNull = true;
        }

        Evidence(subject, "input:" + transform.Signature,
            $"Matches {(best.Column is null ? "control" : "grid column")} '{parameter.Label ?? best.Key}{(best.Column is null ? "" : "/" + best.Column)}' in all {obs.Count} occurrence(s)" +
            (alternatives > 0 ? $"; {alternatives} other control(s) also matched" : "") + ".", confidence);
        return new Binding { Kind = BindingKind.Input, Parameter = parameter.Name, Encoding = transform.Encoding, Confidence = confidence };
    }

    /// <summary>
    /// A typed-in number or date that the app wrote verbatim still deserves a typed parameter; the schema says
    /// whether the target column is numeric or a date.
    /// </summary>
    private Transform Refine(Transform t, List<Observation> obs, string? target)
    {
        var col = TargetColumn(target);
        var clr = col is null ? null : TypeMap.ToClr(col.DataType);
        if (t.Signature is "identity" or "trim" && obs.All(o => o.Value is null || decimal.TryParse(o.Value, NumberStyles.Float, CultureInfo.InvariantCulture, out _)) &&
            (clr is "int" or "long" or "decimal" or "double" || (clr is null && obs.All(o => o.Kind == LiteralKind.Number))))
        {
            var type = clr is "int" or "long" ? clr : clr is "double" ? "double" : obs.Any(o => o.Value?.Contains('.', StringComparison.Ordinal) == true) || clr == "decimal" ? "decimal" : "long";
            return t with { Signature = "number:invariant", Encoding = ValueEncoding.Of(EncodingKind.Number), ClrType = type };
        }

        if (t.Signature is "identity" or "trim" && clr is "DateTime" or "DateOnly" && obs.All(o => o.Value is null || ValueCorrelator.DetectDateFormat(o.Value) is not null))
        {
            var format = obs.Select(o => o.Value).OfType<string>().Select(ValueCorrelator.DetectDateFormat).FirstOrDefault() ?? "yyyy-MM-dd";
            return t with { Signature = "date:" + format, Encoding = ValueEncoding.Of(EncodingKind.DateText, format), ClrType = clr, UiFormat = format };
        }

        if (t.Signature.StartsWith("number:", StringComparison.Ordinal) && clr is "int" or "long" or "decimal" or "double")
        {
            return t with { ClrType = clr };
        }

        if (t.Signature.StartsWith("date:", StringComparison.Ordinal) && clr is "DateOnly")
        {
            return t with { ClrType = "DateOnly" };
        }

        return t;
    }

    private TemplateParameter GetOrCreateParameter(string key, string? gridColumn, string? collection, Transform t, string? target, List<Observation> obs)
    {
        var mapKey = gridColumn is null ? "ctl:" + key : $"grid:{key}:{gridColumn}";
        if (_paramsByKey.TryGetValue(mapKey, out var existing))
        {
            // A parameter created for a condition learns its column constraints once a statement uses it.
            if (existing.TargetColumn is null && TargetColumn(target) is { } known)
            {
                existing.TargetColumn = target;
                existing.Required = !known.Nullable && !known.HasDefault;
                existing.MaxLength = existing.ClrType == "string" ? known.MaxLength : null;
            }

            return existing;
        }

        ControlValue? cv = null;
        if (gridColumn is null)
        {
            cv = _values.Select(v => v.GetValueOrDefault(key)).FirstOrDefault(v => v is not null);
        }

        var col = TargetColumn(target);
        var p = new TemplateParameter
        {
            Name = Naming.Unique(Naming.Identifier(gridColumn ?? cv?.Label, gridColumn is null ? key : null, target?.Split('.')[^1] ?? "value"), _usedNames),
            ClrType = t.ClrType,
            Label = gridColumn ?? cv?.Label,
            AutomationId = gridColumn is null ? key : null,
            Collection = gridColumn is null ? null : collection,
            Column = gridColumn,
            UiKind = gridColumn is not null ? "grid-cell" : cv?.Kind switch
            {
                UiEventKind.Toggled => "toggle",
                UiEventKind.SelectionChanged => "selection",
                _ => "text",
            },
            UiCulture = t.UiCulture,
            UiFormat = t.UiFormat,
            TargetColumn = target,
            MaxLength = t.ClrType == "string" ? col?.MaxLength : null,
            Required = col is not null && !col.Nullable && !col.HasDefault,
        };
        if (col is not null && target is not null)
        {
            var table = _ctx.Schema.Table(target.Split('.')[0]);
            if (table?.ForeignKeys.FirstOrDefault(f => string.Equals(f.Column, col.Name, StringComparison.OrdinalIgnoreCase)) is { } fk)
            {
                p.ForeignKey = fk.ReferencedTable + "." + fk.ReferencedColumn;
            }
        }

        _template.Parameters.Add(p);
        if (collection is not null && gridColumn is not null)
        {
            _template.Collections.First(c => c.Name == collection).Parameters.Add(p.Name);
        }

        _paramsByKey[mapKey] = p;
        return p;
    }

    private Binding? FindDependent(Slot slot, List<Observation> obs, string subject)
    {
        HashSet<(int Step, string Column, bool Plus1)>? candidates = null;
        foreach (var o in obs)
        {
            if (o.Value is null)
            {
                return null;
            }

            var set = new HashSet<(int, string, bool)>();
            foreach (var earlier in _prepared[o.Demo].Where(s => s.Index < o.Statement.Index && s.Event.Result is { Rows.Count: 1 }))
            {
                if (!_owner.TryGetValue((o.Demo, earlier.Index), out var owner))
                {
                    continue;
                }

                // A source inside the same loop must come from the same iteration.
                if (owner.Slot.Loop is not null && (owner.Slot.Loop != slot.Loop || owner.Iteration != o.Iteration))
                {
                    continue;
                }

                var result = earlier.Event.Result!;
                for (var c = 0; c < result.Columns.Count; c++)
                {
                    var name = result.Columns[c].Length == 0 ? "#" + c.ToString(CultureInfo.InvariantCulture) : result.Columns[c];
                    if (HkValue.CanonEquals(result.Rows[0][c], o.Value))
                    {
                        set.Add((owner.Slot.Step.Id, name, false));
                    }
                    else if (decimal.TryParse(result.Rows[0][c], NumberStyles.Float, CultureInfo.InvariantCulture, out var read) &&
                             decimal.TryParse(o.Value, NumberStyles.Float, CultureInfo.InvariantCulture, out var written) && written == read + 1)
                    {
                        set.Add((owner.Slot.Step.Id, name, true));
                    }
                }
            }

            if (candidates is null)
            {
                candidates = set;
            }
            else
            {
                candidates.IntersectWith(set);
            }

            if (candidates.Count == 0)
            {
                return null;
            }
        }

        if (candidates is null || candidates.Count == 0)
        {
            return null;
        }

        var best = candidates.OrderBy(c => c.Plus1).ThenByDescending(c => c.Step).First();
        var recovered = obs.Any(o => _prepared[o.Demo].Any(s => _owner.TryGetValue((o.Demo, s.Index), out var w) && w.Slot.Step.Id == best.Step && s.Event.ResultRecovered));
        Evidence(subject, best.Plus1 ? "dependent:increment" : "dependent", $"Equals column '{best.Column}' returned by step {best.Step}{(best.Plus1 ? " plus one" : "")} in all {obs.Count} occurrence(s){(recovered ? " (results recovered by replaying on the baseline copy)" : "")}.", best.Plus1 ? 0.85 : 0.95);
        return new Binding
        {
            Kind = BindingKind.Dependent,
            SourceStep = best.Step,
            SourceColumn = best.Column,
            Encoding = ValueEncoding.Of(best.Plus1 ? EncodingKind.Increment : EncodingKind.Identity),
            Confidence = best.Plus1 ? 0.85 : 0.95,
        };
    }

    private Binding? FindLookup(Slot slot, List<Observation> obs, string? target, string subject)
    {
        if (slot.Collection is not null)
        {
            return null;
        }

        Dictionary<string, (string Control, int? Cell, LookupInfo Lookup)>? candidates = null;
        foreach (var o in obs)
        {
            if (o.Value is null)
            {
                return null;
            }

            var set = new Dictionary<string, (string, int?, LookupInfo)>(StringComparer.OrdinalIgnoreCase);
            foreach (var cv in _values[o.Demo].Values.Where(v => v.Kind != UiEventKind.Toggled))
            {
                var texts = new List<(int? Cell, string Text)>();
                if (cv.Value is { Length: > 0 })
                {
                    texts.Add((null, cv.Value));
                }

                if (cv.Cells is not null)
                {
                    texts.AddRange(cv.Cells.Select((c, i) => ((int?)i, c)).Where(x => x.c.Length > 0));
                }

                foreach (var (cell, text) in texts)
                {
                    foreach (var lookup in LookupSources(o, target, text))
                    {
                        set.TryAdd($"{cv.Key}:{cell}:{lookup.Table}:{lookup.KeyColumn}:{lookup.DisplayColumn}", (cv.Key, cell, lookup));
                    }
                }
            }

            if (candidates is null)
            {
                candidates = set;
            }
            else
            {
                foreach (var k in candidates.Keys.Where(k => !set.ContainsKey(k)).ToList())
                {
                    candidates.Remove(k);
                }
            }

            if (candidates.Count == 0)
            {
                return null;
            }
        }

        if (candidates is null || candidates.Count == 0)
        {
            return null;
        }

        var best = candidates.Values.First();
        var mapKey = "ctl:" + best.Control + ":" + best.Cell;
        if (!_paramsByKey.TryGetValue(mapKey, out var p))
        {
            var cv = _values.Select(v => v.GetValueOrDefault(best.Control)).First(v => v is not null)!;
            var displayColumn = _ctx.Schema.Table(best.Lookup.Table)?.Column(best.Lookup.DisplayColumn);
            p = new TemplateParameter
            {
                Name = Naming.Unique(Naming.Identifier(cv.Label, best.Control, best.Lookup.DisplayColumn), _usedNames),
                ClrType = "string",
                Label = cv.Label,
                AutomationId = best.Control,
                CellIndex = best.Cell,
                UiKind = "selection",
                Lookup = best.Lookup,
                TargetColumn = target,
                Required = true,
                MaxLength = displayColumn?.MaxLength,
                ForeignKey = best.Lookup.Table + "." + best.Lookup.KeyColumn,
            };
            _template.Parameters.Add(p);
            _paramsByKey[mapKey] = p;
        }

        Evidence(subject, "input:lookup", $"Selected text in '{p.Label ?? best.Control}' maps to {best.Lookup.Table}.{best.Lookup.KeyColumn} through {best.Lookup.DisplayColumn} in all {obs.Count} occurrence(s).", 0.9);
        return new Binding { Kind = BindingKind.Input, Parameter = p.Name, Encoding = ValueEncoding.Of(EncodingKind.Lookup), Confidence = 0.9 };
    }

    private IEnumerable<LookupInfo> LookupSources(Observation o, string? target, string text)
    {
        var demo = _demos[o.Demo];

        // 1) Through the schema: the column is a foreign key (or the table's own key), so search the referenced table.
        if (target is not null)
        {
            var dot = target.LastIndexOf('.');
            var table = _ctx.Schema.Table(target[..dot]);
            var column = target[(dot + 1)..];
            (string Table, string Key)? reference = null;
            if (table?.ForeignKeys.FirstOrDefault(f => string.Equals(f.Column, column, StringComparison.OrdinalIgnoreCase)) is { } fk)
            {
                reference = (SqlNames.Bare(fk.ReferencedTable), fk.ReferencedColumn);
            }
            else if (table is not null && table.PrimaryKey.Count == 1 && string.Equals(table.PrimaryKey[0], column, StringComparison.OrdinalIgnoreCase))
            {
                reference = (table.Name, column);
            }

            if (reference is { } r && _ctx.BeforeSnapshot(demo)?.Table(r.Table) is { } snapshot)
            {
                foreach (var row in snapshot.Rows.Where(row => HkValue.CanonEquals(row.GetValueOrDefault(r.Key), o.Value)))
                {
                    foreach (var (col, value) in row)
                    {
                        if (!string.Equals(col, r.Key, StringComparison.OrdinalIgnoreCase) && HkValue.CanonEquals(value, text))
                        {
                            yield return new LookupInfo { Table = r.Table, KeyColumn = r.Key, DisplayColumn = col };
                        }
                    }
                }
            }
        }

        // 2) Through results the app read earlier (this demonstration, the context before it, earlier demonstrations).
        var earlier = demo.Context
            .Concat(_demos.Take(o.Demo).SelectMany(d => d.Statements))
            .Concat(_prepared[o.Demo].Where(s => s.Index < o.Statement.Index).Select(s => s.Event));
        foreach (var e in earlier.Where(e => e.Result is { Rows.Count: > 0 }))
        {
            var n = SqlNormalizer.For(_ctx.Dialect).Normalize(e.Text, [.. e.Parameters.Select(p => new BoundValue(p.Name, p.Value))]);
            if (n.Tables.Count != 1 || _ctx.Schema.Table(n.Tables[0]) is not { } table)
            {
                continue;
            }

            var result = e.Result!;
            foreach (var row in result.Rows)
            {
                for (var k = 0; k < result.Columns.Count; k++)
                {
                    if (!HkValue.CanonEquals(row[k], o.Value) || table.Column(result.Columns[k]) is null)
                    {
                        continue;
                    }

                    for (var c = 0; c < result.Columns.Count; c++)
                    {
                        if (c != k && HkValue.CanonEquals(row[c], text) && table.Column(result.Columns[c]) is not null)
                        {
                            yield return new LookupInfo { Table = table.Name, KeyColumn = table.Column(result.Columns[k])!.Name, DisplayColumn = table.Column(result.Columns[c])!.Name };
                        }
                    }
                }
            }
        }
    }

    private Condition InferCondition(HashSet<int> presence, int firstStep)
    {
        var keys = _values.SelectMany(v => v.Keys).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        var atoms = new List<(Condition Condition, Func<ControlValue?, bool> Test, int Priority, ControlValue Sample)>();
        foreach (var key in keys)
        {
            var sample = _values.Select(v => v.GetValueOrDefault(key)).First(v => v is not null)!;
            if (sample.Kind == UiEventKind.Toggled)
            {
                atoms.Add((new Condition { Parameter = key, Op = ConditionOp.IsTrue }, v => v?.Value is { } s && HkParse.ParseBool(s) == true, 0, sample));
                atoms.Add((new Condition { Parameter = key, Op = ConditionOp.IsFalse }, v => !(v?.Value is { } s && HkParse.ParseBool(s) == true), 1, sample));
                continue;
            }

            atoms.Add((new Condition { Parameter = key, Op = ConditionOp.IsNotEmpty }, v => !string.IsNullOrEmpty(v?.Value), 2, sample));
            atoms.Add((new Condition { Parameter = key, Op = ConditionOp.IsEmpty }, v => string.IsNullOrEmpty(v?.Value), 3, sample));
            foreach (var value in _values.Select(v => v.GetValueOrDefault(key)?.Value).OfType<string>().Distinct(StringComparer.Ordinal))
            {
                atoms.Add((new Condition { Parameter = key, Op = ConditionOp.EqualsValue, Value = value }, v => v?.Value == value, 4, sample));
                atoms.Add((new Condition { Parameter = key, Op = ConditionOp.NotEqualsValue, Value = value }, v => v?.Value != value, 5, sample));
            }
        }

        var separating = atoms
            .Where(a => Enumerable.Range(0, _demos.Count).All(d => a.Test(_values[d].GetValueOrDefault(a.Condition.Parameter)) == presence.Contains(d)))
            .OrderBy(a => a.Priority)
            .ToList();
        if (separating.Count == 0)
        {
            _template.Issues.Add($"Step {firstStep} occurs in demonstrations {string.Join(", ", presence.Select(d => _demos[d].Index))} only, and no single input separates them from the others.");
            return new Condition { Parameter = "", Op = ConditionOp.IsTrue, Description = "unresolved" };
        }

        var best = separating[0];
        var parameter = ParameterForCondition(best.Sample);
        best.Condition.Parameter = parameter.Name;
        best.Condition.Description = best.Condition.Op switch
        {
            ConditionOp.IsTrue => $"'{parameter.Label ?? parameter.Name}' is checked",
            ConditionOp.IsFalse => $"'{parameter.Label ?? parameter.Name}' is not checked",
            ConditionOp.IsNotEmpty => $"'{parameter.Label ?? parameter.Name}' is filled in",
            ConditionOp.IsEmpty => $"'{parameter.Label ?? parameter.Name}' is empty",
            ConditionOp.EqualsValue => $"'{parameter.Label ?? parameter.Name}' is '{best.Condition.Value}'",
            _ => $"'{parameter.Label ?? parameter.Name}' is not '{best.Condition.Value}'",
        };
        Evidence($"step {firstStep}", "condition", $"Runs when {best.Condition.Description}; {separating.Count - 1} other rule(s) also separate the demonstrations.", separating.Count == 1 ? 0.9 : 0.7);
        return best.Condition;
    }

    private TemplateParameter ParameterForCondition(ControlValue sample)
    {
        var mapKey = "ctl:" + sample.Key;
        if (_paramsByKey.TryGetValue(mapKey, out var p))
        {
            return p;
        }

        p = new TemplateParameter
        {
            Name = Naming.Unique(Naming.Identifier(sample.Label, sample.Key, "flag"), _usedNames),
            ClrType = sample.Kind == UiEventKind.Toggled ? "bool" : "string",
            Label = sample.Label,
            AutomationId = sample.Key,
            UiKind = sample.Kind switch
            {
                UiEventKind.Toggled => "toggle",
                UiEventKind.SelectionChanged => "selection",
                _ => "text",
            },
        };
        _template.Parameters.Add(p);
        _paramsByKey[mapKey] = p;
        return p;
    }

    private void InferFileOutput()
    {
        var withFiles = Enumerable.Range(0, _demos.Count).Where(d => _demos[d].FileEffects.Count > 0).ToList();
        if (withFiles.Count == 0)
        {
            return;
        }

        foreach (var d in withFiles)
        {
            foreach (var f in _demos[d].FileEffects.Where(f => f.Format != "csv"))
            {
                _template.Issues.Add($"Demonstration {_demos[d].Index} wrote {f.Format} file '{f.Path}'; its content diff is recorded, but only CSV exports are generated.");
            }
        }

        var csv = withFiles.Select(d => (Demo: d, Effect: _demos[d].FileEffects.LastOrDefault(f => f.Format == "csv" && f.After is not null))).Where(x => x.Effect is not null).ToList();
        if (csv.Count == 0)
        {
            return;
        }

        foreach (var slot in _slots.Where(s => s.Step.Kind == "SELECT" && s.Collection is null).Reverse())
        {
            var specs = csv.Select(x => slot.Instances.TryGetValue(x.Demo, out var inst) && inst[0].Event.Result is { } r ? FileOutputInference.Match(x.Effect!.After!, r) : null).ToList();
            if (specs.All(s => s is not null))
            {
                var spec = specs.Zip(csv).OrderByDescending(p => p.Second.Effect!.After!.Length).First().First!;
                _template.FileOutput = new FileOutput { SourceStep = slot.Step.Id, Csv = spec, ObservedPaths = [.. csv.Select(x => x.Effect!.Path)] };
                Evidence("file output", "csv", $"The CSV written in {csv.Count} demonstration(s) is the result of step {slot.Step.Id}, delimiter '{spec.Delimiter}', header {(spec.Header ? "yes" : "no")}.", 0.95);
                return;
            }
        }

        _template.Issues.Add("The app wrote a CSV file that no captured query result explains.");
    }

    private void Finish()
    {
        var statements = _template.AllStatements().ToList();
        var used = new HashSet<int>(statements.SelectMany(s => s.Bindings).Where(b => b.Kind == BindingKind.Dependent).Select(b => b.SourceStep!.Value));
        if (_template.FileOutput is not null)
        {
            used.Add(_template.FileOutput.SourceStep);
        }

        ApplyGuards(statements);
        var writes = statements.Where(s => s.Kind != "SELECT").ToList();
        _template.Kind = writes.Count > 0 ? ActionKind.Write : ActionKind.Read;
        _template.Destructive = statements.Any(s => s.Kind == "DELETE");

        if (_template.Kind == ActionKind.Read)
        {
            var result = statements.LastOrDefault(s => s.Kind == "SELECT" && s.Bindings.Any(b => b.Kind == BindingKind.Input)) ?? statements.LastOrDefault();
            if (result is not null)
            {
                _template.ResultStep = result.Id;
                used.Add(result.Id);
                _template.ResultColumns = [.. ResultColumns(result)];
            }
        }

        // SELECTs whose results nothing uses (form refreshes, metadata reads) are dropped so generated code stays minimal.
        var unused = statements.Where(s => s.Kind == "SELECT" && !used.Contains(s.Id)).Select(s => s.Id).ToHashSet();
        Prune(_template.Steps, unused);
        if (unused.Count > 0)
        {
            Evidence("template", "prune", $"Dropped {unused.Count} SELECT statement(s) whose results no later statement, output or file uses: steps {string.Join(", ", unused)}.", 1);
        }

        var tables = _template.AllStatements().SelectMany(s => s.Table is null ? [] : new[] { s.Table })
            .Concat(_template.Parameters.Where(p => p.Lookup is not null).Select(p => p.Lookup!.Table));
        _template.Tables = [.. tables.Distinct(StringComparer.OrdinalIgnoreCase).Order(StringComparer.OrdinalIgnoreCase)];

        foreach (var s in _prepared.SelectMany(p => p).SelectMany(p => p.N.ServerGeneratedColumns))
        {
            if (!_template.GeneratedColumns.Contains(s, StringComparer.OrdinalIgnoreCase))
            {
                _template.GeneratedColumns.Add(s);
            }
        }

        // Columns filled by time- or GUID-based defaults differ on every run and are compared as generated values.
        foreach (var t in _template.Tables)
        {
            foreach (var c in _ctx.Schema.Table(t)?.Columns ?? [])
            {
                var def = c.DefaultExpression?.ToUpperInvariant() ?? "";
                if ((def.Contains("GETDATE", StringComparison.Ordinal) || def.Contains("SYSDATETIME", StringComparison.Ordinal) || def.Contains("NEWID", StringComparison.Ordinal) ||
                     def.Contains("CURRENT_TIMESTAMP", StringComparison.Ordinal) || def.Contains("NOW", StringComparison.Ordinal) || def.Contains("DATETIME(", StringComparison.Ordinal)) &&
                    !_template.GeneratedColumns.Contains(t + "." + c.Name, StringComparer.OrdinalIgnoreCase))
                {
                    _template.GeneratedColumns.Add(t + "." + c.Name);
                }
            }
        }

        _template.SchemaFingerprint = _ctx.Schema.Fingerprint(_template.Tables);
        foreach (var t in _template.Tables)
        {
            _template.TableFingerprints[t] = _ctx.Schema.Fingerprint([t]);
        }
        if (_template.Parameters.Count == 0 && _template.Kind == ActionKind.Write && _demos.Count > 1)
        {
            Evidence("template", "no-inputs", "Every value was identical across demonstrations; the action takes no inputs.", 0.6);
        }
    }

    private void ApplyGuards(List<StatementStep> statements)
    {
        foreach (var s in statements.Where(s => s.GuardRequired))
        {
            var guard = _ctx.Guards.For(s.Shape);
            if (guard is null)
            {
                _template.Issues.Add(
                    $"Step {s.Id} deletes rows, but state-diff capture cannot see the condition the app used to choose them " +
                    $"(for example AND Status = 'Draft'). Supply it with: hokusai guard \"{_template.Name}\" --step {s.Id} --where \"<condition>\" " +
                    "(or --unconditional if the app deletes without one), then verify again.");
                continue;
            }

            s.Guard = guard;
            if (guard != Guards.Unconditional)
            {
                s.Sql += " AND (" + guard + ")";
            }

            Evidence($"step {s.Id}", "guard", guard == Guards.Unconditional ? "The user confirmed the delete has no further condition." : $"User-supplied condition: {guard}.", 1);
        }
    }

    private static void Prune(List<TemplateStep> steps, HashSet<int> remove)
    {
        steps.RemoveAll(s => s is StatementStep st && remove.Contains(st.Id));
        foreach (var s in steps)
        {
            switch (s)
            {
                case LoopStep l:
                    Prune(l.Body, remove);
                    break;
                case ConditionalStep c:
                    Prune(c.Body, remove);
                    break;
            }
        }

        steps.RemoveAll(s => s is LoopStep { Body.Count: 0 } or ConditionalStep { Body.Count: 0 });
    }

    private IEnumerable<ResultColumn> ResultColumns(StatementStep step)
    {
        var slot = _slots.First(s => s.Step.Id == step.Id);
        var samples = slot.Instances.Values.SelectMany(i => i).Select(i => i.Event.Result).OfType<ResultData>().ToList();
        var columns = samples.FirstOrDefault()?.Columns ?? [];
        var tables = slot.Instances.Values.First()[0].N.Tables.Select(t => _ctx.Schema.Table(t)).OfType<TableSchema>().ToList();
        for (var c = 0; c < columns.Count; c++)
        {
            var name = columns[c];
            var schemaColumn = tables.Select(t => t.Column(name)).FirstOrDefault(x => x is not null);
            var values = samples.SelectMany(s => s.Rows.Select(r => c < r.Count ? r[c] : null)).ToList();
            yield return new ResultColumn
            {
                Name = name,
                ClrType = schemaColumn is not null ? TypeMap.ToClr(schemaColumn.DataType) : GuessType(values),
                Nullable = schemaColumn?.Nullable ?? values.Any(v => v is null),
            };
        }
    }

    private static string GuessType(List<string?> values)
    {
        var present = values.OfType<string>().ToList();
        if (present.Count == 0)
        {
            return "string";
        }

        if (present.All(v => long.TryParse(v, NumberStyles.Integer, CultureInfo.InvariantCulture, out _)))
        {
            return "long";
        }

        if (present.All(v => decimal.TryParse(v, NumberStyles.Float, CultureInfo.InvariantCulture, out _)))
        {
            return "decimal";
        }

        return present.All(v => HkValue.TryParseDateTime(v, out _)) ? "DateTime" : "string";
    }
}
