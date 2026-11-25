using System.Text.Json.Serialization;
using Hokusai.Runtime;

namespace Hokusai.Core;

public enum ActionKind
{
    Read,
    Write,
}

public enum TemplateStatus
{
    Unverified,
    Verified,
}

public enum ValueClass
{
    Input,
    Dependent,
    Generated,
    Constant,
}

public sealed class LookupInfo
{
    public string Table { get; set; } = "";
    public string KeyColumn { get; set; } = "";
    public string DisplayColumn { get; set; } = "";
}

/// <summary>An input of the generated action, traced back to the control the user typed into.</summary>
public sealed class TemplateParameter
{
    public string Name { get; set; } = "";
    public string ClrType { get; set; } = "string";
    public int? MaxLength { get; set; }
    public bool Required { get; set; }
    public string? Label { get; set; }
    public string? AutomationId { get; set; }

    /// <summary>text, toggle, selection or grid-cell.</summary>
    public string UiKind { get; set; } = "text";

    /// <summary>Set for line-item parameters: the collection they belong to.</summary>
    public string? Collection { get; set; }

    /// <summary>For grid-cell parameters: the grid column header.</summary>
    public string? Column { get; set; }

    /// <summary>For a selected grid row: which cell held the value (a lookup's display text).</summary>
    public int? CellIndex { get; set; }
    public string? UiCulture { get; set; }

    /// <summary>The app stored NULL when this control was left empty.</summary>
    public bool EmptyAsNull { get; set; }
    public string? UiFormat { get; set; }
    public LookupInfo? Lookup { get; set; }
    public string? TargetColumn { get; set; }
    public string? ForeignKey { get; set; }
}

public sealed class InputList
{
    public string Name { get; set; } = "";
    public string? AutomationId { get; set; }
    public string? Label { get; set; }
    public List<string> Parameters { get; set; } = [];
}

public enum BindingKind
{
    Input,
    Dependent,
    Generated,
    Constant,
}

/// <summary>Where the value of one placeholder comes from at execution time.</summary>
public sealed class Binding
{
    public string Placeholder { get; set; } = "";
    public BindingKind Kind { get; set; }
    public string? Parameter { get; set; }
    public ValueEncoding Encoding { get; set; } = ValueEncoding.Of(EncodingKind.Identity);
    public int? SourceStep { get; set; }
    public string? SourceColumn { get; set; }

    /// <summary>now, utcnow, today or guid.</summary>
    public string? Generator { get; set; }
    public string? Constant { get; set; }

    /// <summary>string, number or null; how <see cref="Constant"/> is typed when bound.</summary>
    public string? ConstantType { get; set; }
    public string? TargetColumn { get; set; }
    public double Confidence { get; set; } = 1;
}

[JsonPolymorphic(TypeDiscriminatorPropertyName = "type")]
[JsonDerivedType(typeof(StatementStep), "statement")]
[JsonDerivedType(typeof(LoopStep), "loop")]
[JsonDerivedType(typeof(ConditionalStep), "conditional")]
public abstract class TemplateStep
{
    public IEnumerable<StatementStep> Statements() => this switch
    {
        StatementStep s => [s],
        LoopStep l => l.Body.SelectMany(b => b.Statements()),
        ConditionalStep c => c.Body.SelectMany(b => b.Statements()),
        _ => [],
    };
}

public sealed class StatementStep : TemplateStep
{
    public int Id { get; set; }

    /// <summary>Statement text with neutral placeholders (@hk0, @hk1, ...).</summary>
    public string Sql { get; set; } = "";
    public string Shape { get; set; } = "";
    public string Kind { get; set; } = "";
    public string? Table { get; set; }
    public List<Binding> Bindings { get; set; } = [];
    public List<string> ResultColumns { get; set; } = [];

    /// <summary>The UI event (by control key) that preceded the statement in the demonstrations.</summary>
    public string? Trigger { get; set; }

    /// <summary>
    /// A destructive statement synthesized from a state diff: the app's condition (for example
    /// <c>AND Status = 'Draft'</c>) left no trace, so the user must state it before the action can be enabled.
    /// </summary>
    public bool GuardRequired { get; set; }

    /// <summary>The user-supplied guard: a SQL predicate appended to the statement, or <see cref="Guards.Unconditional"/>.</summary>
    public string? Guard { get; set; }
}

public sealed class LoopStep : TemplateStep
{
    public string Collection { get; set; } = "";
    public List<TemplateStep> Body { get; set; } = [];
}

public enum ConditionOp
{
    IsTrue,
    IsFalse,
    EqualsValue,
    NotEqualsValue,
    IsEmpty,
    IsNotEmpty,
}

public sealed class Condition
{
    public string Parameter { get; set; } = "";
    public ConditionOp Op { get; set; }
    public string? Value { get; set; }
    public string Description { get; set; } = "";
}

public sealed class ConditionalStep : TemplateStep
{
    public Condition Condition { get; set; } = new();
    public List<TemplateStep> Body { get; set; } = [];
}

public sealed class ResultColumn
{
    public string Name { get; set; } = "";
    public string ClrType { get; set; } = "string";
    public bool Nullable { get; set; } = true;
}

/// <summary>A file the action writes, produced from the result of one of its statements.</summary>
public sealed class FileOutput
{
    public int SourceStep { get; set; }
    public string Format { get; set; } = "csv";
    public CsvSpec Csv { get; set; } = new();

    /// <summary>The path the app wrote in each demonstration; the generated API returns content instead of writing files.</summary>
    public List<string> ObservedPaths { get; set; } = [];
}

public sealed class Evidence
{
    public string Subject { get; set; } = "";
    public string Rule { get; set; } = "";
    public string Detail { get; set; } = "";
    public double Confidence { get; set; }
}

/// <summary>The inferred, replayable description of one action.</summary>
public sealed class ActionTemplate
{
    public string Name { get; set; } = "";
    public string? Description { get; set; }
    public ActionKind Kind { get; set; }
    public bool Destructive { get; set; }
    public ProviderKind Provider { get; set; }
    public string SchemaFingerprint { get; set; } = "";

    /// <summary>Fingerprint of each table the action uses, so schema-check can name the tables that changed.</summary>
    public Dictionary<string, string> TableFingerprints { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    public List<string> Tables { get; set; } = [];
    public List<TemplateParameter> Parameters { get; set; } = [];
    public List<InputList> Collections { get; set; } = [];
    public List<TemplateStep> Steps { get; set; } = [];

    /// <summary>Statement whose result set is the action's output (read actions).</summary>
    public int? ResultStep { get; set; }
    public List<ResultColumn> ResultColumns { get; set; } = [];
    public FileOutput? FileOutput { get; set; }

    /// <summary>"Table.Column" entries holding generated values, excluded when diffs are compared.</summary>
    public List<string> GeneratedColumns { get; set; } = [];
    public List<Evidence> Evidence { get; set; } = [];
    public TemplateStatus Status { get; set; } = TemplateStatus.Unverified;
    public List<string> Issues { get; set; } = [];
    public int DemonstrationCount { get; set; }

    public IEnumerable<StatementStep> AllStatements() => Steps.SelectMany(s => s.Statements());

    public TemplateParameter? Parameter(string name) =>
        Parameters.FirstOrDefault(p => string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase));
}
