using System.Text.RegularExpressions;
using Hokusai.Runtime;
using Microsoft.SqlServer.TransactSql.ScriptDom;

namespace Hokusai.Sql;

/// <summary>
/// T-SQL normalization through ScriptDom. RPC calls captured by Extended Events arrive as
/// <c>exec sp_executesql N'...', N'@P1 int', @P1=5</c> (or <c>sp_prepexec</c>); these are unwrapped so the inner
/// statement is the template and the arguments become bound values.
/// </summary>
public sealed partial class TSqlNormalizer : ISqlNormalizer
{
    public NormalizedStatement Normalize(string sql, IReadOnlyList<BoundValue> bound)
    {
        var parser = new TSql180Parser(initialQuotedIdentifiers: true);
        TSqlFragment fragment;
        IList<ParseError> errors;
        using (var reader = new StringReader(sql))
        {
            fragment = parser.Parse(reader, out errors);
        }

        if (errors.Count > 0)
        {
            // Unparseable batches still align by token shape; the generic tokenizer understands T-SQL quoting.
            var fallback = new GenericNormalizer(SqlDialect.SqlServer).Normalize(sql, bound);
            fallback.Errors.AddRange(errors.Select(e => $"line {e.Line}: {e.Message}"));
            return fallback;
        }

        if (TryUnwrapRpc(fragment, out var inner, out var args))
        {
            return Normalize(inner, args);
        }

        var boundNames = new HashSet<string>(bound.Select(b => "@" + b.Name.TrimStart('@')), StringComparer.OrdinalIgnoreCase);
        var sig = new List<Tok>();
        var sigIndexOfToken = new Dictionary<int, int>();
        var tokens = fragment.ScriptTokenStream;
        for (var i = 0; i < tokens.Count; i++)
        {
            var t = tokens[i];
            var type = t.TokenType switch
            {
                TSqlTokenType.WhiteSpace => TokType.Space,
                TSqlTokenType.SingleLineComment or TSqlTokenType.MultilineComment => TokType.Comment,
                TSqlTokenType.AsciiStringLiteral or TSqlTokenType.UnicodeStringLiteral => TokType.Text,
                TSqlTokenType.Integer or TSqlTokenType.Numeric or TSqlTokenType.Real or TSqlTokenType.Money or TSqlTokenType.HexLiteral => TokType.Number,
                TSqlTokenType.Variable => boundNames.Contains(t.Text) ? TokType.Marker : TokType.Word,
                TSqlTokenType.QuotedIdentifier => TokType.QuotedIdent,
                TSqlTokenType.EndOfFile => TokType.Space,
                _ => t.Text.Length > 0 && (char.IsLetter(t.Text[0]) || t.Text[0] is '_' or '@' or '#') ? TokType.Word : TokType.Punct,
            };
            if (type is TokType.Space or TokType.Comment || t.Text is null)
            {
                continue;
            }

            sigIndexOfToken[i] = sig.Count;
            sig.Add(new Tok(type, t.Text, t.Offset));
        }

        var literalAt = GenericNormalizer.DetectLiterals(sig, bound);
        var visitor = new ColumnVisitor();
        fragment.Accept(visitor);
        var columns = new Dictionary<int, string>();
        foreach (var (tokenIndex, column) in visitor.ByToken)
        {
            if (sigIndexOfToken.TryGetValue(tokenIndex, out var s) && literalAt.ContainsKey(s))
            {
                columns[s] = column;
            }
        }

        var tables = visitor.Targets.Concat(visitor.Tables).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        return GenericNormalizer.Assemble(sql, sig, literalAt, columns, visitor.Generated, visitor.Kind ?? GenericNormalizer.KindOf(sig), tables, []);
    }

    private static bool TryUnwrapRpc(TSqlFragment fragment, out string inner, out List<BoundValue> args)
    {
        inner = "";
        args = [];
        var exec = (fragment as TSqlScript)?.Batches.SelectMany(b => b.Statements).OfType<ExecuteStatement>().FirstOrDefault();
        if (exec?.ExecuteSpecification?.ExecutableEntity is not ExecutableProcedureReference proc)
        {
            return false;
        }

        var name = proc.ProcedureReference?.ProcedureReference?.Name?.BaseIdentifier?.Value;
        var parameters = proc.Parameters;
        int sqlIndex, defsIndex;
        if (string.Equals(name, "sp_executesql", StringComparison.OrdinalIgnoreCase))
        {
            (sqlIndex, defsIndex) = (0, 1);
        }
        else if (string.Equals(name, "sp_prepexec", StringComparison.OrdinalIgnoreCase))
        {
            (sqlIndex, defsIndex) = (2, 1);
        }
        else
        {
            return false;
        }

        if (parameters.Count <= sqlIndex || parameters[sqlIndex].ParameterValue is not StringLiteral text)
        {
            return false;
        }

        inner = text.Value;
        var declared = parameters.Count > defsIndex && parameters[defsIndex].ParameterValue is StringLiteral defs
            ? ParamNameRegex().Matches(defs.Value).Select(m => m.Value).ToList()
            : [];
        var position = 0;
        for (var i = Math.Max(sqlIndex, defsIndex) + 1; i < parameters.Count; i++)
        {
            var p = parameters[i];
            var pname = p.Variable?.Name ?? (position < declared.Count ? declared[position] : "@P" + (position + 1).ToString(System.Globalization.CultureInfo.InvariantCulture));
            args.Add(new BoundValue(pname, ValueOf(p.ParameterValue)));
            position++;
        }

        return true;
    }

    internal static string? ValueOf(ScalarExpression? e) => e switch
    {
        NullLiteral => null,
        StringLiteral s => s.Value,
        IntegerLiteral or NumericLiteral or RealLiteral => HkValue.CanonNumericLiteral(((Literal)e).Value),
        MoneyLiteral m => HkValue.CanonNumericLiteral(m.Value.TrimStart('$')),
        Literal l => l.Value,
        UnaryExpression { UnaryExpressionType: UnaryExpressionType.Negative, Expression: Literal l } => HkValue.CanonNumericLiteral("-" + l.Value),
        _ => null,
    };

    [GeneratedRegex(@"@\w+")]
    private static partial Regex ParamNameRegex();

    private sealed class ColumnVisitor : TSqlFragmentVisitor
    {
        public Dictionary<int, string> ByToken { get; } = [];
        public List<string> Generated { get; } = [];
        public List<string> Tables { get; } = [];
        public List<string> Targets { get; } = [];
        public string? Kind { get; private set; }

        private static string Name(SchemaObjectName? n) =>
            n is null ? "" : SqlNames.Bare(string.Join('.', n.Identifiers.Select(i => i.Value)));

        private static string? TargetName(TableReference? t) => (t as NamedTableReference)?.SchemaObject is { } o ? Name(o) : null;

        private void SetKind(string kind) => Kind ??= kind;

        public override void Visit(InsertStatement node) => SetKind("INSERT");

        public override void Visit(UpdateStatement node) => SetKind("UPDATE");

        public override void Visit(DeleteStatement node) => SetKind("DELETE");

        public override void Visit(MergeStatement node) => SetKind("MERGE");

        public override void Visit(ExecuteStatement node) => SetKind("EXEC");

        public override void Visit(SelectStatement node) => SetKind("SELECT");

        public override void Visit(NamedTableReference node)
        {
            var n = Name(node.SchemaObject);
            if (n.Length > 0)
            {
                Tables.Add(n);
            }
        }

        public override void Visit(InsertSpecification node)
        {
            var table = TargetName(node.Target) ?? "";
            if (table.Length > 0)
            {
                Targets.Add(table);
            }

            var cols = node.Columns.Select(c => c.MultiPartIdentifier.Identifiers[^1].Value).ToList();
            if (node.InsertSource is ValuesInsertSource values)
            {
                foreach (var row in values.RowValues)
                {
                    for (var j = 0; j < row.ColumnValues.Count && j < cols.Count; j++)
                    {
                        Assign(row.ColumnValues[j], table, cols[j]);
                    }
                }
            }
        }

        public override void Visit(UpdateSpecification node)
        {
            var table = TargetName(node.Target) ?? "";
            if (table.Length > 0)
            {
                Targets.Add(table);
            }

            foreach (var clause in node.SetClauses.OfType<AssignmentSetClause>())
            {
                if (clause.Column is not null)
                {
                    Assign(clause.NewValue, table, clause.Column.MultiPartIdentifier.Identifiers[^1].Value);
                }
            }
        }

        public override void Visit(DeleteSpecification node)
        {
            if (TargetName(node.Target) is { } t)
            {
                Targets.Add(t);
            }
        }

        public override void Visit(BooleanComparisonExpression node)
        {
            if (node.FirstExpression is ColumnReferenceExpression c1 && IsValue(node.SecondExpression))
            {
                ByToken[node.SecondExpression.LastTokenIndex] = c1.MultiPartIdentifier.Identifiers[^1].Value;
            }
            else if (node.SecondExpression is ColumnReferenceExpression c2 && IsValue(node.FirstExpression))
            {
                ByToken[node.FirstExpression.LastTokenIndex] = c2.MultiPartIdentifier.Identifiers[^1].Value;
            }
        }

        public override void Visit(LikePredicate node)
        {
            if (node.FirstExpression is ColumnReferenceExpression c && IsValue(node.SecondExpression))
            {
                ByToken[node.SecondExpression.LastTokenIndex] = c.MultiPartIdentifier.Identifiers[^1].Value;
            }
        }

        private static bool IsValue(ScalarExpression? e) =>
            e is Literal or VariableReference or UnaryExpression { Expression: Literal };

        private void Assign(ScalarExpression? value, string table, string column)
        {
            if (IsValue(value))
            {
                ByToken[value!.LastTokenIndex] = column;
            }
            else if ((value is FunctionCall f && SqlNormalizer.IsGeneratorFunction(f.FunctionName.Value)) || value is ParameterlessCall)
            {
                Generated.Add(table + "." + column);
            }
        }
    }
}
