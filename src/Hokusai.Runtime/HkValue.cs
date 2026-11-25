using System.Globalization;
using System.Text;

namespace Hokusai.Runtime;

/// <summary>
/// Canonical text form of database values. Every comparison in Hokusai (correlation, diffs, verification)
/// happens on canonical strings so that 12.50m, 12.5 (double) and "12.5" from a SQL literal compare equal.
/// </summary>
public static class HkValue
{
    public static string? Canon(object? value)
    {
        switch (value)
        {
            case null:
            case DBNull:
                return null;
            case string s:
                return s;
            case bool b:
                return b ? "1" : "0";
            case byte or sbyte or short or ushort or int or uint or long or ulong:
                return Convert.ToString(value, CultureInfo.InvariantCulture);
            case decimal d:
                return CanonDecimal(d);
            case double dbl:
                return CanonDouble(dbl);
            case float f:
                return CanonDouble(f);
            case DateTime dt:
                return CanonDateTime(dt);
            case DateOnly d:
                return d.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
            case TimeOnly t:
                return t.ToString("HH:mm:ss.FFFFFFF", CultureInfo.InvariantCulture).TrimEnd('.');
            case DateTimeOffset dto:
                return CanonDateTime(dto.DateTime) + dto.ToString("zzz", CultureInfo.InvariantCulture);
            case TimeSpan ts:
                return ts.ToString("c", CultureInfo.InvariantCulture);
            case Guid g:
                return g.ToString("D");
            case byte[] bytes:
                return "0x" + Convert.ToHexString(bytes);
            default:
                return Convert.ToString(value, CultureInfo.InvariantCulture);
        }
    }

    public static string CanonDecimal(decimal d)
    {
        var text = d.ToString("0.############################", CultureInfo.InvariantCulture);
        return text == "-0" ? "0" : text;
    }

    public static string CanonDouble(double value)
    {
        if (double.IsFinite(value) && Math.Abs(value) < 7.9e27)
        {
            // Round-trip through decimal so REAL columns compare with DECIMAL literals; 15 significant digits is what a double holds.
            var rounded = decimal.Parse(value.ToString("G15", CultureInfo.InvariantCulture), NumberStyles.Float, CultureInfo.InvariantCulture);
            return CanonDecimal(rounded);
        }

        return value.ToString("R", CultureInfo.InvariantCulture);
    }

    public static string CanonDateTime(DateTime dt)
    {
        var text = dt.ToString("yyyy-MM-dd HH:mm:ss.FFFFFFF", CultureInfo.InvariantCulture);
        return text.EndsWith('.') ? text[..^1] : text;
    }

    /// <summary>Canonicalizes a numeric literal as written in SQL text (for example "12.50" or "-3").</summary>
    public static string CanonNumericLiteral(string literal)
    {
        if (decimal.TryParse(literal, NumberStyles.Float, CultureInfo.InvariantCulture, out var d))
        {
            return CanonDecimal(d);
        }

        return literal;
    }

    /// <summary>Two canonical values are equal when their text matches, or when both are numbers of equal value.</summary>
    public static bool CanonEquals(string? a, string? b)
    {
        if (a is null || b is null)
        {
            return a is null && b is null;
        }

        if (string.Equals(a, b, StringComparison.Ordinal))
        {
            return true;
        }

        if (decimal.TryParse(a, NumberStyles.Float, CultureInfo.InvariantCulture, out var da) &&
            decimal.TryParse(b, NumberStyles.Float, CultureInfo.InvariantCulture, out var db))
        {
            return da == db;
        }

        if (TryParseDateTime(a, out var ta) && TryParseDateTime(b, out var tb))
        {
            return ta == tb;
        }

        return false;
    }

    public static bool TryParseDateTime(string text, out DateTime value)
    {
        string[] formats =
        [
            "yyyy-MM-dd HH:mm:ss.FFFFFFF", "yyyy-MM-dd HH:mm:ss", "yyyy-MM-ddTHH:mm:ss.FFFFFFF", "yyyy-MM-ddTHH:mm:ss",
            "yyyy-MM-dd", "yyyyMMdd HH:mm:ss", "yyyyMMdd",
        ];
        return DateTime.TryParseExact(text, formats, CultureInfo.InvariantCulture, DateTimeStyles.None, out value);
    }

    public static string Describe(string? value) => value is null ? "NULL" : "'" + value + "'";

    /// <summary>Stable hash of a row used when a table has no primary key.</summary>
    public static string RowHash(IEnumerable<KeyValuePair<string, string?>> row)
    {
        var sb = new StringBuilder();
        foreach (var (key, value) in row.OrderBy(p => p.Key, StringComparer.OrdinalIgnoreCase))
        {
            sb.Append(key.ToUpperInvariant()).Append('=').Append(value is null ? "␀" : value.Replace("|", "||", StringComparison.Ordinal)).Append('|');
        }

        return Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(Encoding.UTF8.GetBytes(sb.ToString())));
    }
}
