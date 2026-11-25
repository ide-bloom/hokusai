using System.Text;
using Hokusai.Runtime;

namespace Hokusai.Sql;

internal enum TokType
{
    Space,
    Comment,
    Text,
    QuotedIdent,
    Number,
    Word,
    Marker,
    Punct,
}

internal readonly record struct Tok(TokType Type, string Text, int Start)
{
    public int End => Start + Text.Length;

    public bool Is(string text) => (Type is TokType.Word or TokType.Punct) && string.Equals(Text, text, StringComparison.OrdinalIgnoreCase);

    public bool IsIdent => Type is TokType.Word or TokType.QuotedIdent;

    public string Ident => Type == TokType.QuotedIdent ? SqlNames.Unquote(Text) : Text;
}

/// <summary>
/// Dialect-tolerant tokenizer for SQLite, Access (Jet/ACE) and generic ODBC SQL. It understands quoting styles,
/// comments and parameter markers well enough to lift literals out and map them to columns.
/// </summary>
public sealed class GenericNormalizer(SqlDialect dialect) : ISqlNormalizer
{
    private static readonly HashSet<string> Operators = new(StringComparer.Ordinal) { "=", "<>", "!=", "<", ">", "<=", ">=" };

    internal static List<Tok> Tokenize(string sql, SqlDialect dialect)
    {
        var tokens = new List<Tok>();
        var i = 0;
        while (i < sql.Length)
        {
            var c = sql[i];
            var start = i;
            if (char.IsWhiteSpace(c))
            {
                while (i < sql.Length && char.IsWhiteSpace(sql[i]))
                {
                    i++;
                }

                tokens.Add(new Tok(TokType.Space, sql[start..i], start));
            }
            else if (c == '-' && Peek(sql, i + 1) == '-')
            {
                while (i < sql.Length && sql[i] != '\n')
                {
                    i++;
                }

                tokens.Add(new Tok(TokType.Comment, sql[start..i], start));
            }
            else if (c == '/' && Peek(sql, i + 1) == '*')
            {
                var end = sql.IndexOf("*/", i + 2, StringComparison.Ordinal);
                i = end < 0 ? sql.Length : end + 2;
                tokens.Add(new Tok(TokType.Comment, sql[start..i], start));
            }
            else if (c == '\'' || ((c is 'N' or 'n') && Peek(sql, i + 1) == '\''))
            {
                i = ScanQuoted(sql, c == '\'' ? i : i + 1, '\'');
                tokens.Add(new Tok(TokType.Text, sql[start..i], start));
            }
            else if (c == '"' && dialect == SqlDialect.Access)
            {
                // Jet SQL treats double quotes as string delimiters, unlike ANSI SQL.
                i = ScanQuoted(sql, i, '"');
                tokens.Add(new Tok(TokType.Text, sql[start..i], start));
            }
            else if (c is '"' or '`')
            {
                i = ScanQuoted(sql, i, c);
                tokens.Add(new Tok(TokType.QuotedIdent, sql[start..i], start));
            }
            else if (c == '[')
            {
                var end = sql.IndexOf(']', i);
                i = end < 0 ? sql.Length : end + 1;
                tokens.Add(new Tok(TokType.QuotedIdent, sql[start..i], start));
            }
            else if (c == '#' && dialect == SqlDialect.Access && sql.IndexOf('#', i + 1) is var close && close > i)
            {
                // Access date literals: #2026-11-24#.
                i = close + 1;
                tokens.Add(new Tok(TokType.Text, sql[start..i], start));
            }
            else if (char.IsAsciiDigit(c) || (c == '.' && char.IsAsciiDigit(Peek(sql, i + 1))))
            {
                while (i < sql.Length && (char.IsAsciiDigit(sql[i]) || sql[i] == '.'))
                {
                    i++;
                }

                if (i < sql.Length && (sql[i] is 'e' or 'E') && (char.IsAsciiDigit(Peek(sql, i + 1)) || ((Peek(sql, i + 1) is '+' or '-') && char.IsAsciiDigit(Peek(sql, i + 2)))))
                {
                    i += 2;
                    while (i < sql.Length && char.IsAsciiDigit(sql[i]))
                    {
                        i++;
                    }
                }

                tokens.Add(new Tok(TokType.Number, sql[start..i], start));
            }
            else if (c == '?')
            {
                i++;
                while (i < sql.Length && char.IsAsciiDigit(sql[i]))
                {
                    i++;
                }

                tokens.Add(new Tok(TokType.Marker, sql[start..i], start));
            }
            else if (c == '@' && Peek(sql, i + 1) == '@')
            {
                // @@IDENTITY and other system variables are functions, not parameter markers.
                i += 2;
                while (i < sql.Length && (char.IsLetterOrDigit(sql[i]) || sql[i] == '_'))
                {
                    i++;
                }

                tokens.Add(new Tok(TokType.Word, sql[start..i], start));
            }
            else if ((c is ':' or '@' or '$') && (char.IsLetter(Peek(sql, i + 1)) || Peek(sql, i + 1) == '_'))
            {
                i++;
                while (i < sql.Length && (char.IsLetterOrDigit(sql[i]) || sql[i] == '_'))
                {
                    i++;
                }

                tokens.Add(new Tok(TokType.Marker, sql[start..i], start));
            }
            else if (char.IsLetter(c) || c == '_')
            {
                while (i < sql.Length && (char.IsLetterOrDigit(sql[i]) || sql[i] is '_' or '$'))
                {
                    i++;
                }

                tokens.Add(new Tok(TokType.Word, sql[start..i], start));
            }
            else
            {
                var two = i + 1 < sql.Length ? sql.Substring(i, 2) : "";
                i += two is "<>" or "!=" or "<=" or ">=" or "||" ? 2 : 1;
                tokens.Add(new Tok(TokType.Punct, sql[start..i], start));
            }
        }

        return tokens;
    }

    private static char Peek(string s, int i) => i < s.Length ? s[i] : '\0';

    private static int ScanQuoted(string sql, int openIndex, char quote)
    {
        var i = openIndex + 1;
        while (i < sql.Length)
        {
            if (sql[i] == quote)
            {
                if (Peek(sql, i + 1) == quote)
                {
                    i += 2;
                    continue;
                }

                return i + 1;
            }

            i++;
        }

        return sql.Length;
    }

    internal static string UnquoteString(string text)
    {
        var t = text.StartsWith('N') || text.StartsWith('n') ? text[1..] : text;
        if (t.Length >= 2 && t[0] == '#' && t[^1] == '#')
        {
            return t[1..^1];
        }

        if (t.Length >= 2)
        {
            var q = t[0];
            return t[1..^1].Replace(new string(q, 2), q.ToString(), StringComparison.Ordinal);
        }

        return t;
    }

    public NormalizedStatement Normalize(string sql, IReadOnlyList<BoundValue> bound)
    {
        var all = Tokenize(sql, dialect);
        var sig = all.Where(t => t.Type is not (TokType.Space or TokType.Comment)).ToList();
        var literalAt = DetectLiterals(sig, bound);
        var columns = MapColumns(sig, literalAt.Keys.ToHashSet(), out var generated);
        var tables = TablesOf(sig);
        return Assemble(sql, sig, literalAt, columns, generated, KindOf(sig), tables, []);
    }

    /// <summary>Decides which significant tokens are literal values; a minus sign is folded into the number it negates.</summary>
    internal static Dictionary<int, (int FirstSig, LiteralKind Kind, string? Value)> DetectLiterals(List<Tok> sig, IReadOnlyList<BoundValue> bound)
    {
        var literalAt = new Dictionary<int, (int FirstSig, LiteralKind Kind, string? Value)>();
        var markerOrdinal = 0;
        for (var i = 0; i < sig.Count; i++)
        {
            var t = sig[i];
            switch (t.Type)
            {
                case TokType.Text:
                    literalAt[i] = (i, LiteralKind.Text, UnquoteString(t.Text));
                    break;
                case TokType.Number:
                    var negative = i > 0 && sig[i - 1].Is("-") && (i == 1 || !(sig[i - 2].IsIdent || sig[i - 2].Type == TokType.Number || sig[i - 2].Is(")")));
                    literalAt[i] = (negative ? i - 1 : i, LiteralKind.Number, HkValue.CanonNumericLiteral((negative ? "-" : "") + t.Text.TrimStart('$')));
                    break;
                case TokType.Word when t.Is("NULL") && i > 0 && (sig[i - 1].Is("(") || sig[i - 1].Is(",") || sig[i - 1].Is("=")) && !(i > 1 && sig[i - 1].Is("=") && sig[i - 2].Is("IS")):
                    literalAt[i] = (i, LiteralKind.Null, null);
                    break;
                case TokType.Marker:
                    var name = t.Text.Length > 1 && t.Text[0] != '?' ? t.Text[1..] : null;
                    var bv = (name is null ? null : bound.FirstOrDefault(b => string.Equals(b.Name.TrimStart('@', ':', '$'), name, StringComparison.OrdinalIgnoreCase)))
                        ?? (markerOrdinal < bound.Count ? bound[markerOrdinal] : null);
                    markerOrdinal++;
                    literalAt[i] = (i, LiteralKind.Bound, bv?.Value);
                    break;
            }
        }

        return literalAt;
    }

    internal static NormalizedStatement Assemble(
        string sql,
        List<Tok> sig,
        Dictionary<int, (int FirstSig, LiteralKind Kind, string? Value)> literalAt,
        Dictionary<int, string> columns,
        List<string> generated,
        string kind,
        List<string> tables,
        List<string> errors)
    {
        var literals = new List<SqlLiteral>();
        var template = new StringBuilder();
        var shape = new StringBuilder();
        var cursor = 0;
        var ordered = literalAt.OrderBy(p => p.Key).ToList();
        foreach (var (sigIndex, info) in ordered)
        {
            var startTok = sig[info.FirstSig];
            var endTok = sig[sigIndex];
            template.Append(sql, cursor, startTok.Start - cursor);
            var lit = new SqlLiteral(literals.Count, info.Value, info.Kind, columns.GetValueOrDefault(sigIndex));
            literals.Add(lit);
            template.Append(lit.Placeholder);
            cursor = endTok.End;
        }

        template.Append(sql, cursor, sql.Length - cursor);

        var literalSig = new HashSet<int>(ordered.SelectMany(p => Enumerable.Range(p.Value.FirstSig, p.Key - p.Value.FirstSig + 1)));
        for (var i = 0; i < sig.Count; i++)
        {
            if (literalSig.Contains(i))
            {
                if (literalAt.ContainsKey(i))
                {
                    Append(shape, "?");
                }

                continue;
            }

            var t = sig[i];
            Append(shape, t.Type switch
            {
                TokType.QuotedIdent => SqlNames.Unquote(t.Text).ToUpperInvariant(),
                TokType.Word => t.Text.ToUpperInvariant(),
                _ => t.Text,
            });
        }

        return new NormalizedStatement
        {
            Original = sql,
            Shape = shape.ToString().TrimEnd(';', ' '),
            Template = template.ToString(),
            Literals = literals,
            Kind = kind,
            Table = tables.FirstOrDefault(),
            Tables = tables,
            ServerGeneratedColumns = generated,
            Errors = errors,
        };
    }

    private static void Append(StringBuilder sb, string text)
    {
        if (sb.Length > 0)
        {
            sb.Append(' ');
        }

        sb.Append(text);
    }

    internal static string KindOf(List<Tok> sig)
    {
        var first = sig.FirstOrDefault(t => t.Type == TokType.Word).Text?.ToUpperInvariant() ?? "";
        return first switch
        {
            "INSERT" or "REPLACE" => "INSERT",
            "UPDATE" => "UPDATE",
            "DELETE" => "DELETE",
            "SELECT" or "WITH" or "PRAGMA" or "VALUES" => "SELECT",
            "MERGE" => "MERGE",
            "EXEC" or "EXECUTE" or "CALL" => "EXEC",
            _ => "OTHER",
        };
    }

    private static string ReadName(List<Tok> sig, ref int i)
    {
        var parts = new List<string>();
        while (i < sig.Count && sig[i].IsIdent)
        {
            parts.Add(sig[i].Ident);
            if (i + 1 < sig.Count && sig[i + 1].Is("."))
            {
                i += 2;
                continue;
            }

            i++;
            break;
        }

        return SqlNames.Bare(string.Join('.', parts));
    }

    internal static List<string> TablesOf(List<Tok> sig)
    {
        var tables = new List<string>();
        for (var i = 0; i < sig.Count; i++)
        {
            if (sig[i].Is("INTO") || sig[i].Is("UPDATE") || sig[i].Is("FROM") || sig[i].Is("JOIN"))
            {
                var j = i + 1;
                if (j < sig.Count && sig[j].IsIdent && !sig[j].Is("SELECT"))
                {
                    var name = ReadName(sig, ref j);
                    if (name.Length > 0 && !tables.Contains(name, StringComparer.OrdinalIgnoreCase))
                    {
                        tables.Add(name);
                    }
                }
            }
        }

        return tables;
    }

    internal static Dictionary<int, string> MapColumns(List<Tok> sig, HashSet<int> literalIndexes, out List<string> generated)
    {
        var map = new Dictionary<int, string>();
        generated = [];
        for (var i = 0; i < sig.Count; i++)
        {
            if (sig[i].Is("INTO") && i + 1 < sig.Count)
            {
                var j = i + 1;
                var table = ReadName(sig, ref j);
                if (j >= sig.Count || !sig[j].Is("("))
                {
                    continue;
                }

                var cols = new List<string>();
                j++;
                while (j < sig.Count && !sig[j].Is(")"))
                {
                    if (sig[j].IsIdent)
                    {
                        cols.Add(sig[j].Ident);
                    }

                    j++;
                }

                j++;
                if (j >= sig.Count || !sig[j].Is("VALUES"))
                {
                    continue;
                }

                j++;
                while (j < sig.Count && sig[j].Is("("))
                {
                    var groups = SplitGroup(sig, ref j);
                    for (var g = 0; g < groups.Count && g < cols.Count; g++)
                    {
                        AssignValue(sig, groups[g], literalIndexes, table + "." + cols[g], cols[g], map, generated);
                    }

                    if (j < sig.Count && sig[j].Is(","))
                    {
                        j++;
                    }
                }
            }
            else if (sig[i].Is("UPDATE"))
            {
                var j = i + 1;
                var table = ReadName(sig, ref j);
                if (j >= sig.Count || !sig[j].Is("SET"))
                {
                    continue;
                }

                j++;
                while (j + 1 < sig.Count && sig[j].IsIdent && sig[j + 1].Is("="))
                {
                    var col = sig[j].Ident;
                    j += 2;
                    var start = j;
                    var depth = 0;
                    while (j < sig.Count && !(depth == 0 && (sig[j].Is(",") || sig[j].Is("WHERE") || sig[j].Is(";"))))
                    {
                        depth += sig[j].Is("(") ? 1 : sig[j].Is(")") ? -1 : 0;
                        j++;
                    }

                    AssignValue(sig, (start, j - 1), literalIndexes, table + "." + col, col, map, generated);
                    if (j < sig.Count && sig[j].Is(","))
                    {
                        j++;
                    }
                }
            }
        }

        // Comparisons: column op literal, including LIKE.
        foreach (var li in literalIndexes)
        {
            if (map.ContainsKey(li))
            {
                continue;
            }

            var opIndex = li - 1;
            if (opIndex >= 0 && sig[opIndex].Is("-"))
            {
                opIndex--;
            }

            if (opIndex >= 1 && (Operators.Contains(sig[opIndex].Text) || sig[opIndex].Is("LIKE")) && sig[opIndex - 1].IsIdent)
            {
                map[li] = sig[opIndex - 1].Ident;
            }
        }

        return map;
    }

    private static List<(int Start, int End)> SplitGroup(List<Tok> sig, ref int j)
    {
        var groups = new List<(int, int)>();
        var depth = 0;
        var start = j + 1;
        for (j++; j < sig.Count; j++)
        {
            if (sig[j].Is("("))
            {
                depth++;
            }
            else if (sig[j].Is(")"))
            {
                if (depth == 0)
                {
                    groups.Add((start, j - 1));
                    j++;
                    break;
                }

                depth--;
            }
            else if (depth == 0 && sig[j].Is(","))
            {
                groups.Add((start, j - 1));
                start = j + 1;
            }
        }

        return groups;
    }

    private static void AssignValue(List<Tok> sig, (int Start, int End) range, HashSet<int> literalIndexes, string qualified, string column, Dictionary<int, string> map, List<string> generated)
    {
        if (range.End < range.Start)
        {
            return;
        }

        if (literalIndexes.Contains(range.End) && (range.End == range.Start || (range.End == range.Start + 1 && sig[range.Start].Is("-"))))
        {
            map[range.End] = column;
        }
        else if (sig[range.Start].Type == TokType.Word && SqlNormalizer.IsGeneratorFunction(sig[range.Start].Text) &&
                 (range.Start == range.End || sig[range.Start + 1].Is("(")))
        {
            generated.Add(qualified);
        }
    }
}
