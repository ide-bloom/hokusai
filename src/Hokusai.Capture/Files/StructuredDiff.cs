using System.Text.Json.Nodes;
using System.Xml.Linq;
using Hokusai.Core;
using Hokusai.Runtime;

namespace Hokusai.Capture.Files;

/// <summary>
/// Content diffs for the text formats legacy apps write: INI (section.key), JSON (pointer paths), XML (element and
/// attribute paths), CSV (rows as a multiset) and plain text (lines).
/// </summary>
public static class StructuredDiff
{
    public static string FormatOf(string path, string? content)
    {
        var ext = Path.GetExtension(path).ToUpperInvariant();
        if (content is not null && content.Contains('\0', StringComparison.Ordinal))
        {
            return "binary";
        }

        return ext switch
        {
            ".INI" or ".CFG" or ".INF" => "ini",
            ".CSV" or ".TSV" => "csv",
            ".XML" or ".CONFIG" => "xml",
            ".JSON" => "json",
            _ => "text",
        };
    }

    public static List<ContentChange> Diff(string format, string? before, string? after)
    {
        try
        {
            return format switch
            {
                "ini" => DiffMaps(FlattenIni(before), FlattenIni(after)),
                "json" => DiffMaps(FlattenJson(before), FlattenJson(after)),
                "xml" => DiffMaps(FlattenXml(before), FlattenXml(after)),
                "csv" => DiffRows(before, after),
                "binary" => [new ContentChange("changed", "", null, null)],
                _ => DiffLines(before, after),
            };
        }
        catch (Exception ex) when (ex is System.Xml.XmlException or System.Text.Json.JsonException)
        {
            // Malformed structured content still gets a line diff.
            return DiffLines(before, after);
        }
    }

    private static List<ContentChange> DiffMaps(Dictionary<string, string?> before, Dictionary<string, string?> after)
    {
        var changes = new List<ContentChange>();
        foreach (var (k, v) in after)
        {
            if (!before.TryGetValue(k, out var old))
            {
                changes.Add(new ContentChange("added", k, null, v));
            }
            else if (old != v)
            {
                changes.Add(new ContentChange("changed", k, old, v));
            }
        }

        changes.AddRange(before.Where(p => !after.ContainsKey(p.Key)).Select(p => new ContentChange("removed", p.Key, p.Value, null)));
        return changes;
    }

    public static Dictionary<string, string?> FlattenIni(string? text)
    {
        var map = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
        var section = "";
        foreach (var raw in (text ?? "").Split('\n'))
        {
            var line = raw.Trim();
            if (line.Length == 0 || line[0] is ';' or '#')
            {
                continue;
            }

            if (line[0] == '[' && line[^1] == ']')
            {
                section = line[1..^1].Trim();
                continue;
            }

            var eq = line.IndexOf('=', StringComparison.Ordinal);
            if (eq > 0)
            {
                map[(section.Length > 0 ? section + "." : "") + line[..eq].Trim()] = line[(eq + 1)..].Trim();
            }
        }

        return map;
    }

    public static Dictionary<string, string?> FlattenJson(string? text)
    {
        var map = new Dictionary<string, string?>(StringComparer.Ordinal);
        if (string.IsNullOrWhiteSpace(text))
        {
            return map;
        }

        void Walk(JsonNode? node, string path)
        {
            switch (node)
            {
                case JsonObject o:
                    foreach (var (k, v) in o)
                    {
                        Walk(v, path + "/" + k.Replace("~", "~0", StringComparison.Ordinal).Replace("/", "~1", StringComparison.Ordinal));
                    }

                    break;
                case JsonArray a:
                    for (var i = 0; i < a.Count; i++)
                    {
                        Walk(a[i], path + "/" + i.ToString(System.Globalization.CultureInfo.InvariantCulture));
                    }

                    break;
                case null:
                    map[path] = null;
                    break;
                default:
                    map[path] = node.ToJsonString().Trim('"');
                    break;
            }
        }

        Walk(JsonNode.Parse(text), "");
        return map;
    }

    public static Dictionary<string, string?> FlattenXml(string? text)
    {
        var map = new Dictionary<string, string?>(StringComparer.Ordinal);
        if (string.IsNullOrWhiteSpace(text))
        {
            return map;
        }

        void Walk(XElement e, string path)
        {
            foreach (var a in e.Attributes())
            {
                map[path + "/@" + a.Name.LocalName] = a.Value;
            }

            var children = e.Elements().ToList();
            if (children.Count == 0)
            {
                map[path] = e.Value;
            }

            foreach (var group in children.GroupBy(c => c.Name.LocalName))
            {
                var index = 1;
                foreach (var child in group)
                {
                    Walk(child, $"{path}/{group.Key}[{index++}]");
                }
            }
        }

        var root = XDocument.Parse(text).Root!;
        Walk(root, "/" + root.Name.LocalName);
        return map;
    }

    private static List<ContentChange> DiffRows(string? before, string? after)
    {
        var delimiter = (after ?? before ?? "").Count(c => c == ';') > (after ?? before ?? "").Count(c => c == ',') ? ";" : ",";
        var b = CsvOutput.Parse(before ?? "", delimiter).Select(r => string.Join(delimiter, r)).ToList();
        var changes = new List<ContentChange>();
        var rowIndex = 0;
        foreach (var row in CsvOutput.Parse(after ?? "", delimiter).Select(r => string.Join(delimiter, r)))
        {
            rowIndex++;
            if (!b.Remove(row))
            {
                changes.Add(new ContentChange("added", "row " + rowIndex.ToString(System.Globalization.CultureInfo.InvariantCulture), null, row));
            }
        }

        changes.AddRange(b.Select(r => new ContentChange("removed", "row", r, null)));
        return changes;
    }

    private static List<ContentChange> DiffLines(string? before, string? after)
    {
        var a = (before ?? "").Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n');
        var b = (after ?? "").Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n');
        var lcs = new int[a.Length + 1, b.Length + 1];
        for (var i = a.Length - 1; i >= 0; i--)
        {
            for (var j = b.Length - 1; j >= 0; j--)
            {
                lcs[i, j] = a[i] == b[j] ? lcs[i + 1, j + 1] + 1 : Math.Max(lcs[i + 1, j], lcs[i, j + 1]);
            }
        }

        var changes = new List<ContentChange>();
        int x = 0, y = 0;
        while (x < a.Length || y < b.Length)
        {
            if (x < a.Length && y < b.Length && a[x] == b[y])
            {
                x++;
                y++;
            }
            else if (y < b.Length && (x >= a.Length || lcs[x, y + 1] >= lcs[x + 1, y]))
            {
                changes.Add(new ContentChange("added", "line " + (y + 1).ToString(System.Globalization.CultureInfo.InvariantCulture), null, b[y]));
                y++;
            }
            else
            {
                changes.Add(new ContentChange("removed", "line " + (x + 1).ToString(System.Globalization.CultureInfo.InvariantCulture), a[x], null));
                x++;
            }
        }

        return changes;
    }
}
