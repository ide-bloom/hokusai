using Hokusai.Runtime;

namespace Hokusai.Core;

public enum UiEventKind
{
    Invoke,
    ValueChanged,
    SelectionChanged,
    Toggled,
    GridSnapshot,
}

public sealed class GridData
{
    public List<string> Columns { get; set; } = [];
    public List<List<string?>> Rows { get; set; } = [];
}

/// <summary>One user interaction observed through UI Automation.</summary>
public sealed class UiEvent
{
    public DateTimeOffset Timestamp { get; set; }
    public UiEventKind Kind { get; set; }
    public string? AutomationId { get; set; }
    public string? Name { get; set; }

    /// <summary>The text of the label next to the control, which becomes the parameter's display name.</summary>
    public string? Label { get; set; }
    public string? ControlType { get; set; }
    public string? Value { get; set; }
    public string? Window { get; set; }

    /// <summary>For a selected grid or list row: the text of each cell, any of which may be a lookup's display value.</summary>
    public List<string>? Cells { get; set; }
    public GridData? Grid { get; set; }

    /// <summary>Identity of the control across demonstrations.</summary>
    public string Key => AutomationId is { Length: > 0 } ? AutomationId : Label ?? Name ?? "control";
}

public sealed class StatementParameter
{
    public string Name { get; set; } = "";
    public string? Value { get; set; }
    public string? Type { get; set; }
}

public sealed class ResultData
{
    public List<string> Columns { get; set; } = [];
    public List<List<string?>> Rows { get; set; } = [];

    public string? Value(int row, string column)
    {
        var index = Columns.FindIndex(c => string.Equals(c, column, StringComparison.OrdinalIgnoreCase));
        return index < 0 || row >= Rows.Count ? null : Rows[row][index];
    }

    public static ResultData From(HkResult result) => new()
    {
        Columns = [.. result.Columns],
        Rows = [.. result.Rows.Select(r => r.Select(HkValue.Canon).ToList())],
    };
}

/// <summary>One statement the app sent to its database.</summary>
public sealed class StatementEvent
{
    public DateTimeOffset Timestamp { get; set; }

    /// <summary>"xe" (Extended Events), "odbc" (agent) or "diff" (synthesized from a state diff).</summary>
    public string Source { get; set; } = "";
    public string Text { get; set; } = "";
    public List<StatementParameter> Parameters { get; set; } = [];
    public long? RowCount { get; set; }
    public int? SessionId { get; set; }
    public ResultData? Result { get; set; }

    /// <summary>True when the result set was recovered by replaying the demonstration on its baseline copy.</summary>
    public bool ResultRecovered { get; set; }
}

public enum FileEffectKind
{
    Created,
    Modified,
    Deleted,
}

public sealed record ContentChange(string Kind, string Path, string? Before, string? After);

public sealed class FileEffect
{
    public DateTimeOffset Timestamp { get; set; }
    public string Path { get; set; } = "";
    public FileEffectKind Kind { get; set; }

    /// <summary>ini, csv, xml, json, text or binary.</summary>
    public string Format { get; set; } = "text";
    public string? Before { get; set; }
    public string? After { get; set; }
    public List<ContentChange> Changes { get; set; } = [];
}

/// <summary>Everything captured between one start mark and the matching end mark.</summary>
public sealed class Demonstration
{
    public string Action { get; set; } = "";
    public int Index { get; set; }
    public DateTimeOffset Start { get; set; }
    public DateTimeOffset End { get; set; }
    public int ProcessId { get; set; }
    public ProviderKind Provider { get; set; }

    /// <summary>Culture the app ran under; number and date inputs are parsed with it first.</summary>
    public string? Culture { get; set; }
    public List<UiEvent> UiEvents { get; set; } = [];
    public List<StatementEvent> Statements { get; set; } = [];

    /// <summary>Statements captured since the previous mark (for example the query that filled a combo box), used for lookups.</summary>
    public List<StatementEvent> Context { get; set; } = [];
    public DatabaseDiff? StateDiff { get; set; }
    public List<FileEffect> FileEffects { get; set; } = [];

    /// <summary>File name, inside the demonstration folder, of the database copy taken at the start mark.</summary>
    public string? Baseline { get; set; }
    public List<string> Tables { get; set; } = [];
}
