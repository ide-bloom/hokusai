namespace Hokusai.Core;

/// <summary>A control's value at the moment the action was committed, plus the grid rows visible then.</summary>
public sealed record ControlValue(string Key, string? Label, string? AutomationId, UiEventKind Kind, string? Value, IReadOnlyList<string>? Cells, string? ControlType);

/// <summary>
/// Reconstructs what the form showed when the user committed the action. Forms often clear themselves after
/// saving, so values are read as of the commit time rather than the end of the demonstration.
/// </summary>
public static class UiState
{
    /// <summary>
    /// The first write statement (or, for read-only demonstrations, the last statement). Without statements
    /// (state-diff capture) it is the last button press of the demonstration.
    /// </summary>
    public static DateTimeOffset CommitTime(Demonstration demo)
    {
        var write = demo.Statements.FirstOrDefault(s => !IsSelect(s.Text));
        if (write is not null)
        {
            return write.Timestamp;
        }

        if (demo.Statements.Count > 0)
        {
            return demo.Statements[^1].Timestamp;
        }

        var lastInvoke = demo.UiEvents.LastOrDefault(e => e.Kind == UiEventKind.Invoke);
        return lastInvoke?.Timestamp ?? demo.End;
    }

    public static bool IsSelect(string sql)
    {
        var t = sql.TrimStart();
        return t.StartsWith("SELECT", StringComparison.OrdinalIgnoreCase) || t.StartsWith("WITH", StringComparison.OrdinalIgnoreCase) || t.StartsWith("PRAGMA", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Last value of every scalar control (text, toggle, selection) at or before <paramref name="at"/>.</summary>
    public static Dictionary<string, ControlValue> ValuesAt(Demonstration demo, DateTimeOffset at)
    {
        var values = new Dictionary<string, ControlValue>(StringComparer.OrdinalIgnoreCase);
        foreach (var e in demo.UiEvents.Where(e => e.Timestamp <= at).OrderBy(e => e.Timestamp))
        {
            if (e.Kind is UiEventKind.ValueChanged or UiEventKind.Toggled or UiEventKind.SelectionChanged)
            {
                values[e.Key] = new ControlValue(e.Key, e.Label ?? e.Name, e.AutomationId, e.Kind, e.Value, e.Cells, e.ControlType);
            }
        }

        return values;
    }

    /// <summary>Last snapshot of every grid at or before <paramref name="at"/>.</summary>
    public static Dictionary<string, (UiEvent Source, GridData Grid)> GridsAt(Demonstration demo, DateTimeOffset at)
    {
        var grids = new Dictionary<string, (UiEvent, GridData)>(StringComparer.OrdinalIgnoreCase);
        foreach (var e in demo.UiEvents.Where(e => e.Kind == UiEventKind.GridSnapshot && e.Grid is not null && e.Timestamp <= at).OrderBy(e => e.Timestamp))
        {
            grids[e.Key] = (e, e.Grid!);
        }

        return grids;
    }

    /// <summary>The UI event that most recently preceded a data event, used to link statements to clicks.</summary>
    public static UiEvent? Preceding(Demonstration demo, DateTimeOffset at) =>
        demo.UiEvents.Where(e => e.Kind == UiEventKind.Invoke && e.Timestamp <= at).MaxBy(e => e.Timestamp);
}
