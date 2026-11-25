# Inference

`hokusai infer <action>` turns the demonstrations of one action into a template (`recordings/<action>/template.json`).
Every rule below has unit tests in `tests/Hokusai.Tests/Infer` and `tests/Hokusai.Tests/Sql`, and every decision is
stored as evidence (subject, rule, detail, confidence) for review with `hokusai infer` or in the Studio.

## 1. Normalization

Statements are parsed and their literals replaced by neutral placeholders (`@hk0`, `@hk1`, …).

- **T-SQL** (`Sql.TSqlNormalizer`, ScriptDom `TSql180Parser`): string, Unicode, numeric, money and hex literals;
  `NULL` in value positions (but not in `IS NULL`); negative numbers folded into one literal (but not subtraction).
  RPCs captured as `exec sp_executesql N'…', N'@P1 int', @P1=…` or `sp_prepexec` are unwrapped: the inner statement
  is the template and the arguments are bound values (named or positional). The AST maps each literal to its column
  (`INSERT` column lists, `UPDATE … SET`, comparisons and `LIKE`), finds the target table and the statement kind, and
  marks columns assigned from `GETDATE()`, `SYSDATETIME()`, `NEWID()`, `CURRENT_TIMESTAMP` and similar as generated.
  Unparseable batches fall back to the tokenizer and the parse error is recorded.
- **SQLite, Access and generic ODBC** (`Sql.GenericNormalizer`): a tokenizer that knows `'…'`, `N'…'`, `"…"` (a string
  in Access, an identifier elsewhere), `[…]` and `` `…` `` quoting, Access `#date#` literals, comments, `?` markers
  (bound in order), named `:p`, `@p`, `$p` markers (bound by name) and `@@IDENTITY`-style system variables.
- The **shape** used for alignment upper-cases keywords and identifiers, removes identifier quoting, collapses
  whitespace and writes every literal as `?`.

## 2. Alignment (`Infer.Aligner`)

- Blocks of up to eight statements repeated back to back in any demonstration become **loop** candidates; in every
  demonstration each run of a candidate (even a single occurrence) is folded into one loop token.
- Demonstrations are merged progressively with a longest-common-subsequence alignment of the tokens.
- A position present in only some demonstrations is **conditional**; consecutive conditional statements present in
  the same demonstrations share one condition.
- A loop is tied to the **grid** whose number of non-empty rows equals the loop's iteration count in every
  demonstration (including zero). With no such grid and the same count everywhere, the block is unrolled into fixed
  statements; otherwise an issue is reported.
- SELECT statements whose results nothing uses (form refreshes, metadata reads) are dropped from the template.

## 3. Value classification

For each placeholder, all its values across demonstrations (and loop iterations) are classified, in this order:

1. **Constant**: the same everywhere. Exception: if an earlier single-row result in the same demonstration explains
   the value, it is dependent (identity values often coincide because every demonstration starts from the same
   baseline).
2. **Input**: some control, under one transform, explains the value in every demonstration. For loop statements,
   the grid column of the same row is tried first. Transforms, each offered whenever it holds:
   - identity, trim, upper case, lower case;
   - numbers parsed in the app's culture or a configured culture (`1.234,50` in de-DE is 1234.5), with currency
     symbols removed;
   - dates parsed in those cultures and written in the literal's format (`24.11.2026` → `'2026-11-24'`);
   - booleans for check boxes: `1/0`, `-1/0` (VB), `Y/N`, `T/F`, `True/False`, `true/false`, `yes/no`, `J/N`;
   - LIKE patterns: `%x%`, `x%`, `%x`;
   - an empty field stored as `NULL`.
   When the schema says the column is numeric or a date and the user typed the stored text verbatim, the parameter
   still gets a numeric or date type. If several controls explain the value, the first is chosen and the confidence
   lowered.
3. **Dependent**: equals a column of a single-row result returned by an earlier statement in the same demonstration
   (same iteration for statements inside a loop), or that value plus one (a counter written back incremented).
4. **Lookup input**: a selected combo box or grid text maps to the value through a table: either the table the
   column references by foreign key (or the statement's own table when the column is its key), searched in the
   snapshot taken at the start mark, or a single-table result the app read earlier (in this demonstration, the
   statements captured since the previous mark, or earlier demonstrations). The parameter takes the display text;
   generated code resolves it with a parameterized `SELECT key FROM table WHERE display = @value` and rejects zero or
   several matches.
5. **Generated**: timestamps within ten minutes of the demonstration (local or UTC), today's date, or GUIDs; the
   literal's exact text format is kept, and the column is excluded when verification compares diffs.
6. A value seen only once that matches nothing is assumed **constant** with low confidence; anything else is an
   **issue** and the action stays unverified.

## 4. Conditions

For a conditional group, Hokusai looks for the simplest rule over one control's value at commit time that is true
exactly in the demonstrations containing the statements, preferring: check box checked / not checked, field filled
in / empty, field equals / differs from a value. If no single rule separates them, an issue is reported.

## 5. Parameters

Names come from the control's label (`Credit limit:` → `creditLimit`), else its automation ID without the usual
prefix (`txtCreditLimit`), else the column. Types, maximum lengths, required flags and foreign keys come from the
schema of the target column and the transform.

## 6. Read actions

An action without write statements is a read action. Its result is the last SELECT bound to an input (or the last
SELECT); result columns are typed from the schema of the statement's tables, or from the observed values.

## CSV exports

A CSV file written in every demonstration is matched to a SELECT result (`Infer.FileOutputInference`): delimiter
(`,`, `;`, tab, `|`), header row, quoting, line endings, and for each column the result column and format (plain,
`0.00`, `0.0`, `0`, `0.##`, `#,##0.00`, `yyyy-MM-dd`, `dd.MM.yyyy`, `MM/dd/yyyy`, `dd/MM/yyyy`,
`yyyy-MM-dd HH:mm:ss`, ISO). Other file formats are reported as issues.

## Destructive actions

A DELETE rebuilt from a state diff requires a user-supplied guard before the action can be verified (see
[state-diff.md](state-diff.md#guards-for-destructive-statements)). An action containing a DELETE is flagged destructive; generated MCP tools are annotated as destructive and every write
tool requires `confirm: true`.
