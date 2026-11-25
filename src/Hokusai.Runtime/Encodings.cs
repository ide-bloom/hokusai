using System.Globalization;
using System.Text;

namespace Hokusai.Runtime;

/// <summary>How a typed parameter value becomes the exact value the app wrote to the database.</summary>
public enum EncodingKind
{
    Identity,
    Trim,
    Upper,
    Lower,
    Number,
    DateText,
    BoolMap,
    LikeContains,
    LikeStartsWith,
    LikeEndsWith,
    Lookup,

    /// <summary>The value plus one: a counter read before an insert and written back incremented.</summary>
    Increment,
}

public sealed class ValueEncoding
{
    public EncodingKind Kind { get; set; }

    /// <summary>Date format for <see cref="EncodingKind.DateText"/>, or "true|false" values for <see cref="EncodingKind.BoolMap"/>.</summary>
    public string? Format { get; set; }

    public static ValueEncoding Of(EncodingKind kind, string? format = null) => new() { Kind = kind, Format = format };

    public override string ToString() => Format is null ? Kind.ToString() : $"{Kind}({Format})";
}

public static class HkEncode
{
    /// <summary>Applies an encoding. Lookups are resolved against the database by the caller before encoding.</summary>
    public static object? Encode(ValueEncoding encoding, object? value)
    {
        if (value is null)
        {
            return null;
        }

        switch (encoding.Kind)
        {
            case EncodingKind.Identity:
            case EncodingKind.Lookup:
                return value;
            case EncodingKind.Trim:
                return Str(value).Trim();
            case EncodingKind.Upper:
                return Str(value).ToUpperInvariant();
            case EncodingKind.Lower:
                return Str(value).ToLowerInvariant();
            case EncodingKind.Number:
                return value switch
                {
                    decimal or int or long or short or byte => value,
                    double d => (decimal)d,
                    float f => (decimal)f,
                    _ => decimal.Parse(Str(value), NumberStyles.Float, CultureInfo.InvariantCulture),
                };
            case EncodingKind.DateText:
                var format = encoding.Format ?? "yyyy-MM-dd";
                return value switch
                {
                    DateOnly d => d.ToDateTime(TimeOnly.MinValue).ToString(format, CultureInfo.InvariantCulture),
                    DateTime dt => dt.ToString(format, CultureInfo.InvariantCulture),
                    DateTimeOffset dto => dto.ToString(format, CultureInfo.InvariantCulture),
                    _ => Str(value),
                };
            case EncodingKind.BoolMap:
                var parts = (encoding.Format ?? "1|0").Split('|');
                var chosen = ToBool(value) ? parts[0] : parts[^1];
                return int.TryParse(chosen, NumberStyles.Integer, CultureInfo.InvariantCulture, out var n) ? n : chosen;
            case EncodingKind.LikeContains:
                return "%" + Str(value) + "%";
            case EncodingKind.LikeStartsWith:
                return Str(value) + "%";
            case EncodingKind.LikeEndsWith:
                return "%" + Str(value);
            case EncodingKind.Increment:
                return (value is decimal dv ? dv : decimal.Parse(Str(value), NumberStyles.Float, CultureInfo.InvariantCulture)) + 1;
            default:
                throw new ArgumentOutOfRangeException(nameof(encoding));
        }
    }

    private static string Str(object value) => Convert.ToString(value, CultureInfo.InvariantCulture) ?? "";

    private static bool ToBool(object value) => value switch
    {
        bool b => b,
        string s => HkParse.ParseBool(s) ?? false,
        _ => Convert.ToInt64(value, CultureInfo.InvariantCulture) != 0,
    };
}

/// <summary>Turns text the user typed into a typed parameter value, the way the app's culture read it.</summary>
public static class HkParse
{
    public static object? FromUi(string? text, string clrType, string? culture, string? format)
    {
        if (text is null)
        {
            return null;
        }

        var ci = culture is null ? CultureInfo.InvariantCulture : CultureInfo.GetCultureInfo(culture);
        switch (clrType)
        {
            case "string":
                return text;
            case "bool":
                return ParseBool(text);
            case "int":
            case "long":
            case "decimal":
            case "double":
                if (string.IsNullOrWhiteSpace(text))
                {
                    return null;
                }

                var cleaned = StripCurrency(text, ci);
                var d = decimal.Parse(cleaned, NumberStyles.Number | NumberStyles.AllowCurrencySymbol, ci);
                return clrType switch
                {
                    "int" => (object)decimal.ToInt32(d),
                    "long" => decimal.ToInt64(d),
                    "double" => (double)d,
                    _ => d,
                };
            case "DateTime":
            case "DateOnly":
                if (string.IsNullOrWhiteSpace(text))
                {
                    return null;
                }

                var dt = format is null ? DateTime.Parse(text, ci) : DateTime.ParseExact(text, format, ci);
                return clrType == "DateOnly" ? DateOnly.FromDateTime(dt) : dt;
            case "Guid":
                return Guid.Parse(text);
            default:
                return text;
        }
    }

    public static string StripCurrency(string text, CultureInfo ci)
    {
        var s = text.Trim();
        var symbol = ci.NumberFormat.CurrencySymbol;
        if (symbol.Length > 0)
        {
            s = s.Replace(symbol, "", StringComparison.Ordinal);
        }

        foreach (var c in "$€£¥")
        {
            s = s.Replace(c.ToString(), "", StringComparison.Ordinal);
        }

        return s.Trim();
    }

    public static bool? ParseBool(string text) => text.Trim().ToUpperInvariant() switch
    {
        "ON" or "1" or "-1" or "TRUE" or "CHECKED" or "YES" or "Y" or "T" => true,
        "OFF" or "0" or "FALSE" or "UNCHECKED" or "NO" or "N" or "F" => false,
        _ => null,
    };
}

/// <summary>Formatting of one exported column, as inferred from the file the app wrote.</summary>
public sealed class CsvColumn
{
    public string SourceColumn { get; set; } = "";
    public string? Format { get; set; }
}

public sealed class CsvSpec
{
    public string Delimiter { get; set; } = ",";
    public bool Header { get; set; } = true;
    public List<string> HeaderNames { get; set; } = [];
    public string NewLine { get; set; } = "\r\n";
    public bool QuoteAll { get; set; }
    public bool TrailingNewLine { get; set; } = true;
    public List<CsvColumn> Columns { get; set; } = [];
}

public static class CsvOutput
{
    public static string Write(CsvSpec spec, HkResult result)
    {
        var sb = new StringBuilder();
        var lines = new List<string>();
        if (spec.Header)
        {
            lines.Add(string.Join(spec.Delimiter, spec.HeaderNames.Select(h => Quote(h, spec))));
        }

        foreach (var row in result.Rows)
        {
            var cells = spec.Columns.Select(c =>
            {
                var index = result.Columns.FindIndex(n => string.Equals(n, c.SourceColumn, StringComparison.OrdinalIgnoreCase));
                return Quote(FormatValue(index < 0 ? null : row[index], c.Format), spec);
            });
            lines.Add(string.Join(spec.Delimiter, cells));
        }

        sb.AppendJoin(spec.NewLine, lines);
        if (spec.TrailingNewLine && lines.Count > 0)
        {
            sb.Append(spec.NewLine);
        }

        return sb.ToString();
    }

    /// <summary>
    /// Formats a value with an inferred .NET format string. "n:" prefixes a numeric format, "d:" a date format;
    /// no format means the canonical text.
    /// </summary>
    public static string FormatValue(object? value, string? format)
    {
        if (value is null)
        {
            return "";
        }

        if (format is null)
        {
            return HkValue.Canon(value) ?? "";
        }

        if (format.StartsWith("n:", StringComparison.Ordinal))
        {
            var d = value is string s ? decimal.Parse(s, NumberStyles.Float, CultureInfo.InvariantCulture) : Convert.ToDecimal(value, CultureInfo.InvariantCulture);
            return d.ToString(format[2..], CultureInfo.InvariantCulture);
        }

        if (format.StartsWith("d:", StringComparison.Ordinal))
        {
            var dt = value switch
            {
                DateTime t => t,
                DateOnly o => o.ToDateTime(TimeOnly.MinValue),
                DateTimeOffset o => o.DateTime,
                string s when HkValue.TryParseDateTime(s, out var p) => p,
                _ => Convert.ToDateTime(value, CultureInfo.InvariantCulture),
            };
            return dt.ToString(format[2..], CultureInfo.InvariantCulture);
        }

        return HkValue.Canon(value) ?? "";
    }

    private static string Quote(string value, CsvSpec spec)
    {
        var needs = spec.QuoteAll || value.Contains(spec.Delimiter, StringComparison.Ordinal) || value.Contains('"', StringComparison.Ordinal) || value.Contains('\n', StringComparison.Ordinal) || value.Contains('\r', StringComparison.Ordinal);
        return needs ? "\"" + value.Replace("\"", "\"\"", StringComparison.Ordinal) + "\"" : value;
    }

    /// <summary>Parses CSV text into rows of cells, honoring quotes.</summary>
    public static List<List<string>> Parse(string text, string delimiter)
    {
        var rows = new List<List<string>>();
        var row = new List<string>();
        var cell = new StringBuilder();
        var quoted = false;
        for (var i = 0; i < text.Length; i++)
        {
            var c = text[i];
            if (quoted)
            {
                if (c == '"')
                {
                    if (i + 1 < text.Length && text[i + 1] == '"')
                    {
                        cell.Append('"');
                        i++;
                    }
                    else
                    {
                        quoted = false;
                    }
                }
                else
                {
                    cell.Append(c);
                }

                continue;
            }

            if (c == '"' && cell.Length == 0)
            {
                quoted = true;
            }
            else if (string.CompareOrdinal(text, i, delimiter, 0, delimiter.Length) == 0)
            {
                row.Add(cell.ToString());
                cell.Clear();
                i += delimiter.Length - 1;
            }
            else if (c is '\r' or '\n')
            {
                if (c == '\r' && i + 1 < text.Length && text[i + 1] == '\n')
                {
                    i++;
                }

                row.Add(cell.ToString());
                cell.Clear();
                rows.Add(row);
                row = [];
            }
            else
            {
                cell.Append(c);
            }
        }

        if (cell.Length > 0 || row.Count > 0)
        {
            row.Add(cell.ToString());
            rows.Add(row);
        }

        return rows;
    }
}
