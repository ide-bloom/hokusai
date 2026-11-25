using System.Globalization;
using Hokusai.Core;
using Hokusai.Runtime;

namespace Hokusai.Infer;

/// <summary>A way to turn what the user typed into the value the app wrote.</summary>
public sealed record Transform(string Signature, ValueEncoding Encoding, string ClrType, string? UiCulture, string? UiFormat, int Rank, bool EmptyAsNull = false);

/// <summary>
/// Searches the transforms that map a UI value onto a statement value: identity, trimming, case changes,
/// locale-aware numbers and dates, boolean encodings and LIKE patterns. Lookups and dependent values need data
/// and are handled by <see cref="TemplateInferrer"/>.
/// </summary>
public static class ValueCorrelator
{
    internal static readonly string[] DateOutputFormats =
    [
        "yyyy-MM-dd", "yyyy-MM-dd HH:mm:ss", "yyyy-MM-ddTHH:mm:ss", "yyyyMMdd", "dd.MM.yyyy", "MM/dd/yyyy", "dd/MM/yyyy", "yyyy-MM-dd 00:00:00",
    ];

    internal static readonly string[][] BoolFamilies =
    [
        ["1", "0"], ["-1", "0"], ["Y", "N"], ["T", "F"], ["True", "False"], ["true", "false"], ["TRUE", "FALSE"], ["yes", "no"], ["J", "N"],
    ];

    public static List<Transform> Transforms(string? ui, string? literal, UiEventKind kind, IReadOnlyList<string> cultures)
    {
        var list = new List<Transform>();
        if (ui is null)
        {
            return list;
        }

        if (literal is null)
        {
            if (ui.Trim().Length == 0)
            {
                list.Add(new Transform("empty-null", ValueEncoding.Of(EncodingKind.Identity), "string", null, null, 9, EmptyAsNull: true));
            }

            return list;
        }

        if (kind == UiEventKind.Toggled)
        {
            var state = HkParse.ParseBool(ui);
            if (state is null)
            {
                return list;
            }

            var rank = 0;
            foreach (var family in BoolFamilies)
            {
                if (string.Equals(state.Value ? family[0] : family[1], literal, StringComparison.Ordinal))
                {
                    list.Add(new Transform("bool:" + family[0] + "|" + family[1], ValueEncoding.Of(EncodingKind.BoolMap, family[0] + "|" + family[1]), "bool", null, null, rank));
                }

                rank++;
            }

            return list;
        }

        // Each transform is offered whenever it holds, even trivially ("Hooli".Trim() == "Hooli"), so that a demonstration
        // where the user typed clean text and one with stray spaces still agree on "trim".
        if (string.Equals(ui, literal, StringComparison.Ordinal))
        {
            list.Add(new Transform("identity", ValueEncoding.Of(EncodingKind.Identity), "string", null, null, 0));
        }

        if (string.Equals(ui.Trim(), literal, StringComparison.Ordinal))
        {
            list.Add(new Transform("trim", ValueEncoding.Of(EncodingKind.Trim), "string", null, null, 1));
        }

        if (string.Equals(ui.ToUpperInvariant(), literal, StringComparison.Ordinal))
        {
            list.Add(new Transform("upper", ValueEncoding.Of(EncodingKind.Upper), "string", null, null, 2));
        }

        if (string.Equals(ui.ToLowerInvariant(), literal, StringComparison.Ordinal))
        {
            list.Add(new Transform("lower", ValueEncoding.Of(EncodingKind.Lower), "string", null, null, 2));
        }

        if (ui.Length > 0)
        {
            if (literal == "%" + ui + "%")
            {
                list.Add(new Transform("like:contains", ValueEncoding.Of(EncodingKind.LikeContains), "string", null, null, 3));
            }
            else if (literal == ui + "%")
            {
                list.Add(new Transform("like:starts", ValueEncoding.Of(EncodingKind.LikeStartsWith), "string", null, null, 3));
            }
            else if (literal == "%" + ui)
            {
                list.Add(new Transform("like:ends", ValueEncoding.Of(EncodingKind.LikeEndsWith), "string", null, null, 3));
            }
        }

        var cultureRank = 0;
        foreach (var name in cultures.Prepend("").Distinct(StringComparer.OrdinalIgnoreCase))
        {
            var ci = name.Length == 0 ? CultureInfo.InvariantCulture : CultureInfo.GetCultureInfo(name);
            var cultureName = name.Length == 0 ? null : name;
            if (decimal.TryParse(literal, NumberStyles.Float, CultureInfo.InvariantCulture, out var lit) &&
                decimal.TryParse(HkParse.StripCurrency(ui, ci), NumberStyles.Number | NumberStyles.AllowCurrencySymbol, ci, out var typed) &&
                typed == lit)
            {
                var clr = typed == decimal.Truncate(typed) && !ui.Contains(ci.NumberFormat.NumberDecimalSeparator, StringComparison.Ordinal) ? "long" : "decimal";
                list.Add(new Transform("number:" + (cultureName ?? "invariant"), ValueEncoding.Of(EncodingKind.Number), clr, cultureName, null, 4 + cultureRank));
            }

            if (DateTime.TryParse(ui, ci, DateTimeStyles.AllowWhiteSpaces, out var date) && LooksLikeDate(ui))
            {
                foreach (var format in DateOutputFormats)
                {
                    if (string.Equals(date.ToString(format, CultureInfo.InvariantCulture), literal, StringComparison.Ordinal))
                    {
                        list.Add(new Transform($"date:{cultureName ?? "invariant"}:{format}", ValueEncoding.Of(EncodingKind.DateText, format), "DateTime", cultureName, null, 6 + cultureRank));
                    }
                }
            }

            cultureRank++;
        }

        return list;
    }

    private static bool LooksLikeDate(string ui) => ui.Count(char.IsAsciiDigit) >= 4 && ui.Any(c => c is '.' or '/' or '-' or ' ');

    /// <summary>
    /// A generated value: a timestamp within ten minutes of the demonstration, today's date, or a fresh GUID.
    /// Returns the generator and the encoding that reproduces the literal's text form.
    /// </summary>
    public static (string Generator, ValueEncoding Encoding)? Generated(string? literal, Demonstration demo)
    {
        if (literal is null)
        {
            return null;
        }

        if (Guid.TryParse(literal, out _) && literal.Length >= 32)
        {
            var upper = literal.Any(char.IsAsciiLetterUpper);
            return ("guid", ValueEncoding.Of(upper ? EncodingKind.Upper : EncodingKind.Lower));
        }

        var format = DetectDateFormat(literal);
        if (format is null || !DateTime.TryParseExact(literal, format, CultureInfo.InvariantCulture, DateTimeStyles.None, out var value))
        {
            return null;
        }

        var margin = TimeSpan.FromMinutes(10);
        var localStart = demo.Start.LocalDateTime - margin;
        var localEnd = demo.End.LocalDateTime + margin;
        if (!format.Contains('H', StringComparison.Ordinal))
        {
            return value.Date == demo.Start.LocalDateTime.Date || value.Date == demo.End.LocalDateTime.Date
                ? ("today", ValueEncoding.Of(EncodingKind.DateText, format))
                : null;
        }

        if (value >= localStart && value <= localEnd)
        {
            return ("now", ValueEncoding.Of(EncodingKind.DateText, format));
        }

        if (value >= demo.Start.UtcDateTime - margin && value <= demo.End.UtcDateTime + margin)
        {
            return ("utcnow", ValueEncoding.Of(EncodingKind.DateText, format));
        }

        return null;
    }

    internal static string? DetectDateFormat(string literal)
    {
        string[] formats =
        [
            "yyyy-MM-dd HH:mm:ss.fffffff", "yyyy-MM-dd HH:mm:ss.ffffff", "yyyy-MM-dd HH:mm:ss.fff", "yyyy-MM-dd HH:mm:ss.ff", "yyyy-MM-dd HH:mm:ss.f",
            "yyyy-MM-dd HH:mm:ss", "yyyy-MM-ddTHH:mm:ss.fff", "yyyy-MM-ddTHH:mm:ss", "yyyyMMdd HH:mm:ss", "yyyy-MM-dd HH:mm", "yyyy-MM-dd", "yyyyMMdd",
        ];
        return formats.FirstOrDefault(f => DateTime.TryParseExact(literal, f, CultureInfo.InvariantCulture, DateTimeStyles.None, out _));
    }
}
