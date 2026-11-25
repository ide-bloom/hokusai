using Hokusai.Runtime;

namespace Hokusai.Core;

/// <summary>Rebuilds the typed inputs of a demonstration from what the user entered, using a template's parameter map.</summary>
public static class DemonstrationInputs
{
    public static ActionInput Build(ActionTemplate template, Demonstration demo)
    {
        var input = new ActionInput();
        var at = UiState.CommitTime(demo);
        var values = UiState.ValuesAt(demo, at);
        var grids = UiState.GridsAt(demo, at);

        foreach (var p in template.Parameters.Where(p => p.Collection is null))
        {
            var key = p.AutomationId ?? p.Label ?? p.Name;
            values.TryGetValue(key, out var cv);
            string? text = cv?.Value;
            if (p.CellIndex is int cell)
            {
                text = cv?.Cells is { } cells && cell < cells.Count ? cells[cell] : null;
            }

            input.Scalars[p.Name] = Parse(text, p);
        }

        foreach (var collection in template.Collections)
        {
            var rows = new List<Dictionary<string, object?>>();
            if (grids.TryGetValue(collection.AutomationId ?? collection.Name, out var g))
            {
                foreach (var row in g.Grid.Rows.Where(r => r.Any(c => !string.IsNullOrEmpty(c))))
                {
                    var item = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
                    foreach (var p in template.Parameters.Where(p => p.Collection == collection.Name))
                    {
                        var index = g.Grid.Columns.FindIndex(c => string.Equals(c, p.Column, StringComparison.OrdinalIgnoreCase));
                        item[p.Name] = Parse(index >= 0 && index < row.Count ? row[index] : null, p);
                    }

                    rows.Add(item);
                }
            }

            input.Collections[collection.Name] = rows;
        }

        return input;
    }

    private static object? Parse(string? text, TemplateParameter p)
    {
        if (p.EmptyAsNull && string.IsNullOrEmpty(text))
        {
            return null;
        }

        if (p.ClrType == "bool")
        {
            return text is null ? null : HkParse.ParseBool(text);
        }

        if (string.IsNullOrEmpty(text) && p.ClrType != "string")
        {
            return null;
        }

        return HkParse.FromUi(text, p.ClrType, p.UiCulture, p.UiFormat);
    }
}
