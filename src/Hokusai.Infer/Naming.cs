using System.Text;

namespace Hokusai.Infer;

/// <summary>Builds C#-friendly parameter names from UI labels and automation IDs.</summary>
public static class Naming
{
    private static readonly string[] ControlPrefixes = ["txt", "cbo", "cmb", "chk", "dtp", "num", "lst", "grd", "dgv", "opt", "rb", "btn", "lbl", "tb", "cb"];

    public static string Identifier(string? label, string? key, string fallback)
    {
        var source = Clean(label);
        if (source.Length == 0)
        {
            source = StripPrefix(key ?? "");
        }

        if (source.Length == 0)
        {
            source = fallback;
        }

        return Camel(source);
    }

    private static string Clean(string? label) => (label ?? "").Replace("&", "", StringComparison.Ordinal).Trim().TrimEnd(':', '*', '?').Trim();

    public static string StripPrefix(string key)
    {
        foreach (var p in ControlPrefixes)
        {
            if (key.Length > p.Length && key.StartsWith(p, StringComparison.OrdinalIgnoreCase) && char.IsUpper(key[p.Length]))
            {
                return key[p.Length..];
            }
        }

        return key;
    }

    public static string Camel(string text)
    {
        var words = new List<string>();
        var current = new StringBuilder();
        for (var i = 0; i < text.Length; i++)
        {
            var c = text[i];
            if (!char.IsLetterOrDigit(c))
            {
                Flush();
                continue;
            }

            if (char.IsUpper(c) && current.Length > 0 && (char.IsLower(text[i - 1]) || (i + 1 < text.Length && char.IsLower(text[i + 1]) && char.IsUpper(text[i - 1]))))
            {
                Flush();
            }

            current.Append(c);
        }

        Flush();
        if (words.Count == 0)
        {
            return "value";
        }

        var sb = new StringBuilder(words[0].ToLowerInvariant());
        foreach (var w in words.Skip(1))
        {
            sb.Append(char.ToUpperInvariant(w[0])).Append(w[1..].ToLowerInvariant());
        }

        var result = sb.ToString();
        return char.IsDigit(result[0]) ? "p" + result : result;

        void Flush()
        {
            if (current.Length > 0)
            {
                words.Add(current.ToString());
                current.Clear();
            }
        }
    }

    public static string Pascal(string camel) => camel.Length == 0 ? camel : char.ToUpperInvariant(camel[0]) + camel[1..];

    public static string Unique(string name, ISet<string> used)
    {
        var candidate = name;
        var n = 2;
        while (!used.Add(candidate))
        {
            candidate = name + n.ToString(System.Globalization.CultureInfo.InvariantCulture);
            n++;
        }

        return candidate;
    }
}
