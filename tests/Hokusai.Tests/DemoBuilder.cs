using Hokusai.Core;
using Hokusai.Runtime;

namespace Hokusai.Tests;

/// <summary>Builds demonstrations the way the recorder would, with a clock that advances one second per event.</summary>
internal sealed class DemoBuilder
{
    private readonly Demonstration _demo;
    private DateTimeOffset _clock;

    public DemoBuilder(string action, int index, ProviderKind provider = ProviderKind.SqlServer, string? culture = "en-US")
    {
        _clock = new DateTimeOffset(2026, 11, 24, 10, 0, 0, TimeSpan.Zero).AddMinutes(index * 5);
        _demo = new Demonstration { Action = action, Index = index, Start = _clock, Provider = provider, Culture = culture };
    }

    public DateTimeOffset Now => _clock;

    private DateTimeOffset Tick() => _clock = _clock.AddSeconds(1);

    public DemoBuilder Text(string automationId, string label, string value)
    {
        _demo.UiEvents.Add(new UiEvent { Timestamp = Tick(), Kind = UiEventKind.ValueChanged, AutomationId = automationId, Label = label, ControlType = "Edit", Value = value });
        return this;
    }

    public DemoBuilder Toggle(string automationId, string label, bool on)
    {
        _demo.UiEvents.Add(new UiEvent { Timestamp = Tick(), Kind = UiEventKind.Toggled, AutomationId = automationId, Label = label, ControlType = "CheckBox", Value = on ? "On" : "Off" });
        return this;
    }

    public DemoBuilder Select(string automationId, string label, string value, params string[] cells)
    {
        _demo.UiEvents.Add(new UiEvent
        {
            Timestamp = Tick(),
            Kind = UiEventKind.SelectionChanged,
            AutomationId = automationId,
            Label = label,
            ControlType = cells.Length > 0 ? "DataGrid" : "ComboBox",
            Value = value,
            Cells = cells.Length > 0 ? [.. cells] : null,
        });
        return this;
    }

    public DemoBuilder Grid(string automationId, string label, string[] columns, params string?[][] rows)
    {
        _demo.UiEvents.Add(new UiEvent
        {
            Timestamp = Tick(),
            Kind = UiEventKind.GridSnapshot,
            AutomationId = automationId,
            Label = label,
            ControlType = "DataGrid",
            Grid = new GridData { Columns = [.. columns], Rows = [.. rows.Select(r => r.ToList())] },
        });
        return this;
    }

    public DemoBuilder Click(string automationId, string name)
    {
        _demo.UiEvents.Add(new UiEvent { Timestamp = Tick(), Kind = UiEventKind.Invoke, AutomationId = automationId, Name = name, ControlType = "Button" });
        return this;
    }

    public DemoBuilder Sql(string text, ResultData? result = null, params (string Name, string? Value)[] parameters)
    {
        _demo.Statements.Add(new StatementEvent
        {
            Timestamp = Tick(),
            Source = _demo.Provider == ProviderKind.SqlServer ? "xe" : "odbc",
            Text = text,
            Result = result,
            Parameters = [.. parameters.Select(p => new StatementParameter { Name = p.Name, Value = p.Value })],
        });
        return this;
    }

    public DemoBuilder Context(string text, ResultData result)
    {
        _demo.Context.Add(new StatementEvent { Timestamp = _demo.Start.AddSeconds(-30), Source = "xe", Text = text, Result = result });
        return this;
    }

    public DemoBuilder Diff(DatabaseDiff diff)
    {
        _demo.StateDiff = diff;
        return this;
    }

    public DemoBuilder File(string path, string format, string? before, string after)
    {
        _demo.FileEffects.Add(new FileEffect { Timestamp = Tick(), Path = path, Format = format, Before = before, After = after, Kind = before is null ? FileEffectKind.Created : FileEffectKind.Modified });
        return this;
    }

    public Demonstration Build()
    {
        _demo.End = Tick();
        return _demo;
    }

    public static ResultData Result(string[] columns, params string?[][] rows) => new() { Columns = [.. columns], Rows = [.. rows.Select(r => r.ToList())] };
}
