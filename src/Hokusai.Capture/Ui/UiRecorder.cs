using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using FlaUI.Core.AutomationElements;
using FlaUI.Core.Definitions;
using FlaUI.UIA3;
using Hokusai.Core;

namespace Hokusai.Capture.Ui;

/// <summary>
/// Records what the user does in the target process through UI Automation: button presses, text entered (with the
/// control's label and automation ID), selections, toggles and grid contents. Values are polled as well as
/// observed through events, because legacy Win32 controls raise UIA change events inconsistently.
/// </summary>
public sealed partial class UiRecorder : IDisposable
{
    private readonly int _processId;
    private readonly TimeSpan _pollInterval;
    private readonly UIA3Automation _automation = new();
    private readonly Lock _gate = new();
    private readonly Lock _scanGate = new();
    private readonly List<UiEvent> _events = [];
    private readonly Dictionary<string, string> _lastValues = new(StringComparer.Ordinal);
    private readonly HashSet<IntPtr> _hookedWindows = [];
    private readonly CancellationTokenSource _stop = new();
    private NativeInput? _input;
    private Task? _poller;
    private bool _emitAll;

    public UiRecorder(int processId, TimeSpan? pollInterval = null)
    {
        _processId = processId;
        _pollInterval = pollInterval ?? TimeSpan.FromMilliseconds(200);
    }

    /// <summary>The last failure of a UI scan; scanning continues after failures.</summary>
    public string? LastError { get; private set; }

    public int ErrorCount { get; private set; }

    /// <summary>Raised for every recorded event (for live timelines).</summary>
    public event Action<UiEvent>? Recorded;

    public void Start()
    {
        _emitAll = true;
        _input = new NativeInput(OnClick, OnKey);
        _poller = Task.Run(PollAsync);
    }

    /// <summary>Emits the current value of every control at the next scan, so a demonstration knows the starting state.</summary>
    public void EmitSnapshot()
    {
        lock (_gate)
        {
            _emitAll = true;
        }

        ScanOnce();
    }

    /// <summary>Returns and clears the events recorded so far.</summary>
    public List<UiEvent> Drain()
    {
        lock (_gate)
        {
            var copy = _events.OrderBy(e => e.Timestamp).ToList();
            _events.Clear();
            return copy;
        }
    }

    private void Emit(UiEvent e)
    {
        lock (_gate)
        {
            var same = e.Kind == UiEventKind.Invoke
                ? _events.FirstOrDefault(x => x.Kind == UiEventKind.Invoke && x.Key == e.Key && (e.Timestamp - x.Timestamp).Duration() < TimeSpan.FromMilliseconds(400))
                : null;
            if (same is not null)
            {
                // The same press seen by both the UIA event and the input hook; the hook's earlier time is the true one.
                if (e.Timestamp < same.Timestamp)
                {
                    same.Timestamp = e.Timestamp;
                }

                return;
            }

            _events.Add(e);
        }

        Recorded?.Invoke(e);
    }

    /// <summary>Some legacy providers (for example grid rows) do not even support ControlType; treat those as unknown.</summary>
    private static ControlType TypeOf(AutomationElement e) => e.Properties.ControlType.TryGetValue(out var t) ? t : ControlType.Unknown;

    private static string SafeName(AutomationElement e) => e.Properties.Name.TryGetValue(out var n) ? n ?? "" : "";

    private static bool IsUiaFailure(Exception ex) =>
        ex is COMException or InvalidOperationException or TimeoutException or UnauthorizedAccessException or ArgumentException ||
        ex.GetType().Namespace == "FlaUI.Core.Exceptions";

    private async Task PollAsync()
    {
        while (!_stop.IsCancellationRequested)
        {
            ScanOnce();
            try
            {
                await Task.Delay(_pollInterval, _stop.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }
        }
    }

    private void ScanOnce()
    {
        // The poller and EmitSnapshot can both scan; one scan at a time keeps the change tracking consistent.
        lock (_scanGate)
        {
            ScanCore();
        }
    }

    private void ScanCore()
    {
        try
        {
            var windows = _automation.GetDesktop().FindAllChildren(cf => cf.ByProcessId(_processId));
            bool emitAll;
            lock (_gate)
            {
                emitAll = _emitAll;
                _emitAll = false;
            }

            foreach (var window in windows)
            {
                HookWindow(window);
                ScanWindow(window, emitAll);
            }
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            // Windows and controls appear and disappear while the user works, and UI Automation providers of
            // legacy controls fail in many ways; a failed scan must never stop the recording, so it is counted
            // and the next scan sees the new state.
            LastError = ex.GetType().Name + ": " + ex.Message;
            ErrorCount++;
        }
    }

    private void HookWindow(AutomationElement window)
    {
        var handle = window.Properties.NativeWindowHandle.ValueOrDefault;
        if (!_hookedWindows.Add(handle))
        {
            return;
        }

        window.RegisterAutomationEvent(_automation.EventLibrary.Invoke.InvokedEvent, TreeScope.Descendants, (element, _) =>
        {
            try
            {
                Emit(Describe(element, UiEventKind.Invoke, null));
            }
            catch (Exception ex) when (IsUiaFailure(ex))
            {
                // The button can be gone already (for example a dialog's OK button).
            }
        });
    }

    private void OnClick(int x, int y, DateTimeOffset at)
    {
        try
        {
            var element = _automation.FromPoint(new System.Drawing.Point(x, y));
            for (var i = 0; element is not null && i < 3; i++, element = element.Parent)
            {
                if (element.Properties.ProcessId.ValueOrDefault != _processId)
                {
                    return;
                }

                if (TypeOf(element) is ControlType.Button or ControlType.MenuItem or ControlType.Hyperlink)
                {
                    var e = Describe(element, UiEventKind.Invoke, null);
                    e.Timestamp = at;
                    Emit(e);
                    return;
                }
            }
        }
        catch (Exception ex) when (IsUiaFailure(ex))
        {
            // Clicks outside accessible elements are ignored.
        }
    }

    private void OnKey(DateTimeOffset at)
    {
        try
        {
            var focused = _automation.FocusedElement();
            if (focused?.Properties.ProcessId.ValueOrDefault == _processId && TypeOf(focused) == ControlType.Button)
            {
                var e = Describe(focused, UiEventKind.Invoke, null);
                e.Timestamp = at;
                Emit(e);
            }
        }
        catch (Exception ex) when (IsUiaFailure(ex))
        {
            // Focus moved away before the key was processed.
        }
    }

    private void ScanWindow(AutomationElement window, bool emitAll)
    {
        var all = window.FindAllDescendants();
        var typed = all.Select(e => (Element: e, Type: TypeOf(e))).ToList();
        var grids = typed.Where(e => e.Type is ControlType.DataGrid or ControlType.Table).Select(e => e.Element).ToList();
        var gridRects = grids.Select(g => g.BoundingRectangle).ToList();
        var labels = typed.Where(e => e.Type == ControlType.Text).Select(e => (Name: SafeName(e.Element), e.Element.BoundingRectangle)).Where(l => !string.IsNullOrWhiteSpace(l.Name)).ToList();
        var windowName = window.Name;

        foreach (var grid in grids)
        {
            try
            {
                ScanGrid(grid, windowName, labels, emitAll);
            }
            catch (Exception ex) when (IsUiaFailure(ex))
            {
                LastError = ex.GetType().Name + ": " + ex.Message;
                ErrorCount++;
            }
        }

        foreach (var (element, type) in typed)
        {
            try
            {
                ScanElement(element, type, gridRects, windowName, labels, emitAll);
            }
            catch (Exception ex) when (IsUiaFailure(ex))
            {
                // One misbehaving element must not hide the others.
                LastError = ex.GetType().Name + ": " + ex.Message;
                ErrorCount++;
            }
        }
    }

    private void ScanGrid(AutomationElement grid, string windowName, List<(string Name, System.Drawing.Rectangle Rect)> labels, bool emitAll)
    {
        {
            var read = ReadGrid(grid);
            var data = read.Grid;
            Change(grid, UiEventKind.GridSnapshot, string.Join("\u001E", data.Rows.Select(r => string.Join("\u001F", r))), windowName, labels, emitAll, e => e.Grid = data);
            var selected = read.SelectedRow;
            if (selected is not null)
            {
                Change(grid, UiEventKind.SelectionChanged, string.Join("\u001F", selected), windowName, labels, emitAll, e =>
                {
                    e.Value = selected.FirstOrDefault(c => !string.IsNullOrEmpty(c));
                    e.Cells = [.. selected.Select(c => c ?? "")];
                });
            }
        }

    }

    private void ScanElement(AutomationElement element, ControlType type, List<System.Drawing.Rectangle> gridRects, string windowName, List<(string Name, System.Drawing.Rectangle Rect)> labels, bool emitAll)
    {
        var rect = element.BoundingRectangle;
        if (gridRects.Any(g => g.Contains(rect) && g != rect))
        {
            return; // cells and editing controls inside a grid are covered by the grid snapshot
        }

        switch (type)
        {
            case ControlType.Edit when !element.Properties.IsPassword.ValueOrDefault:
            case ControlType.Spinner:
            case ControlType.Document:
                if (element.Patterns.Value.TryGetPattern(out var value))
                {
                    Change(element, UiEventKind.ValueChanged, value.Value.ValueOrDefault ?? "", windowName, labels, emitAll);
                }

                break;
            case ControlType.ComboBox:
                Change(element, UiEventKind.SelectionChanged, ComboText(element), windowName, labels, emitAll);
                break;
            case ControlType.CheckBox when element.Patterns.Toggle.TryGetPattern(out var toggle):
                Change(element, UiEventKind.Toggled, toggle.ToggleState.ValueOrDefault.ToString(), windowName, labels, emitAll);
                break;
            case ControlType.RadioButton when element.Patterns.SelectionItem.TryGetPattern(out var item):
                Change(element, UiEventKind.Toggled, item.IsSelected.ValueOrDefault ? "On" : "Off", windowName, labels, emitAll);
                break;
            case ControlType.List when element.Patterns.Selection.TryGetPattern(out var selection):
                Change(element, UiEventKind.SelectionChanged, string.Join(", ", selection.Selection.ValueOrDefault?.Select(s => s.Name) ?? []), windowName, labels, emitAll);
                break;
            case ControlType.Pane or ControlType.Custom when !string.IsNullOrEmpty(element.AutomationId) && element.Patterns.Value.TryGetPattern(out var other):
                // Date pickers and similar controls expose their text through the Value pattern only.
                Change(element, UiEventKind.ValueChanged, other.Value.ValueOrDefault ?? "", windowName, labels, emitAll);
                break;
        }
    }

    private static string ComboText(AutomationElement combo)
    {
        if (combo.Patterns.Selection.TryGetPattern(out var selection) && selection.Selection.ValueOrDefault is { Length: > 0 } items)
        {
            return items[0].Name;
        }

        return combo.Patterns.Value.TryGetPattern(out var value) ? value.Value.ValueOrDefault ?? "" : combo.Name;
    }

    private void Change(AutomationElement element, UiEventKind kind, string value, string window, List<(string Name, System.Drawing.Rectangle Rect)> labels, bool emitAll, Action<UiEvent>? fill = null)
    {
        var id = Key(element) + "|" + kind;
        bool changed;
        lock (_gate)
        {
            changed = !_lastValues.TryGetValue(id, out var last) || last != value;
            _lastValues[id] = value;
        }

        if (!changed && !emitAll)
        {
            return;
        }

        var e = Describe(element, kind, value, labels);
        e.Window = window;
        fill?.Invoke(e);
        Emit(e);
    }

    private static string Key(AutomationElement e) => !string.IsNullOrEmpty(e.AutomationId) ? e.AutomationId : TypeOf(e) + ":" + SafeName(e);

    private static UiEvent Describe(AutomationElement element, UiEventKind kind, string? value, List<(string Name, System.Drawing.Rectangle Rect)>? labels = null) => new()
    {
        Timestamp = DateTimeOffset.Now,
        Kind = kind,
        AutomationId = string.IsNullOrEmpty(element.AutomationId) ? null : element.AutomationId,
        Name = SafeName(element),
        Label = Label(element, value, labels),
        ControlType = TypeOf(element).ToString(),
        Value = value,
    };

    /// <summary>
    /// The label a user would read next to the control: the accessible name when it is not just the content,
    /// LabeledBy, or the nearest text element to the left of (or above) the control.
    /// </summary>
    private static string? Label(AutomationElement element, string? value, List<(string Name, System.Drawing.Rectangle Rect)>? labels)
    {
        var name = SafeName(element);
        if (!string.IsNullOrWhiteSpace(name) && name != value && TypeOf(element) != ControlType.Text)
        {
            return name;
        }

        if (element.Properties.LabeledBy.ValueOrDefault is { } labeledBy && !string.IsNullOrWhiteSpace(labeledBy.Name))
        {
            return labeledBy.Name;
        }

        if (labels is null)
        {
            return null;
        }

        var r = element.BoundingRectangle;
        var left = labels.Where(l => l.Rect.Right <= r.Left + 4 && l.Rect.Bottom > r.Top && l.Rect.Top < r.Bottom).OrderBy(l => r.Left - l.Rect.Right).FirstOrDefault();
        if (left.Name is not null)
        {
            return left.Name;
        }

        var above = labels.Where(l => l.Rect.Bottom <= r.Top + 4 && l.Rect.Left < r.Right && l.Rect.Right > r.Left).OrderBy(l => r.Top - l.Rect.Bottom).FirstOrDefault();
        return above.Name;
    }

    internal sealed class GridRead
    {
        public GridData Grid { get; } = new();
        public List<string?>? SelectedRow { get; set; }
    }

    [GeneratedRegex(@"\s+Row\s+\d+.*$", RegexOptions.IgnoreCase)]
    private static partial Regex RowSuffix();

    /// <summary>Reads column headers and every row; works with the Grid/Table patterns or plain row/cell children.</summary>
    internal static GridRead ReadGrid(AutomationElement grid)
    {
        var read = new GridRead();
        var data = read.Grid;
        var rows = grid.FindAllChildren().Where(c => TypeOf(c) is ControlType.DataItem or ControlType.ListItem or ControlType.Custom or ControlType.Pane or ControlType.Unknown).ToList();
        if (grid.Patterns.Table.TryGetPattern(out var table) && table.ColumnHeaders.ValueOrDefault is { Length: > 0 } headers)
        {
            data.Columns.AddRange(headers.Select(h => h.Name));
        }

        foreach (var row in rows)
        {
            var cells = row.FindAllChildren().Where(c => TypeOf(c) is not ControlType.Header and not ControlType.HeaderItem).ToList();
            if (cells.Count == 0 || SafeName(row).Contains("Top Row", StringComparison.OrdinalIgnoreCase))
            {
                if (data.Columns.Count == 0)
                {
                    data.Columns.AddRange(cells.Select(SafeName));
                }

                continue;
            }

            // Row header cells ("Row 0") carry no data.
            cells = [.. cells.Where(c => !Regex.IsMatch(SafeName(c), @"^Row\s+\d+$", RegexOptions.IgnoreCase))];
            if (data.Columns.Count == 0)
            {
                data.Columns.AddRange(cells.Select(c => RowSuffix().Replace(SafeName(c), "")));
            }

            var values = cells.Select(CellText).Select(v => v == "(null)" ? null : v).ToList();
            data.Rows.Add(values);
            if (row.Patterns.SelectionItem.TryGetPattern(out var item) && item.IsSelected.ValueOrDefault)
            {
                read.SelectedRow = values;
            }
        }

        return read;
    }

    /// <summary>A cell's text: the Value pattern, then MSAA's value (WinForms on .NET Framework), then the name.</summary>
    private static string? CellText(AutomationElement cell)
    {
        if (cell.Patterns.Value.TryGetPattern(out var value))
        {
            return value.Value.ValueOrDefault;
        }

        if (cell.Patterns.LegacyIAccessible.TryGetPattern(out var legacy))
        {
            return legacy.Value.ValueOrDefault;
        }

        return SafeName(cell);
    }

    public void Dispose()
    {
        _stop.Cancel();
        try
        {
            _poller?.Wait(TimeSpan.FromSeconds(2));
        }
        catch (AggregateException)
        {
            // The poller ends with cancellation.
        }

        _input?.Dispose();
        _automation.UnregisterAllEvents();
        _automation.Dispose();
        _stop.Dispose();
    }
}
