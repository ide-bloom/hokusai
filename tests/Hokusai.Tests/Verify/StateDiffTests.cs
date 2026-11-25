using Hokusai.Runtime;
using static Hokusai.Tests.TestSchema;

namespace Hokusai.Tests.Verify;

public sealed class StateDiffTests
{
    private static DatabaseSnapshot Snap(string table, string[] keys, params Dictionary<string, string?>[] rows) =>
        new() { Tables = [new TableSnapshot { Table = table, KeyColumns = [.. keys], Columns = [.. rows.SelectMany(r => r.Keys).Distinct()], Rows = [.. rows] }] };

    [Fact]
    public void Keyed_diff_finds_inserts_updates_and_deletes()
    {
        var before = Snap("T", ["Id"], Row(("Id", "1"), ("A", "x")), Row(("Id", "2"), ("A", "y")), Row(("Id", "3"), ("A", "z")));
        var after = Snap("T", ["Id"], Row(("Id", "1"), ("A", "x")), Row(("Id", "2"), ("A", "Y")), Row(("Id", "4"), ("A", "w")));

        var diff = StateDiffer.Diff(before, after);

        var changes = Assert.Single(diff.Tables).Changes;
        Assert.Equal(3, changes.Count);
        Assert.Equal(["A"], changes.Single(c => c.Kind == ChangeKind.Update).ChangedColumns());
        Assert.Equal("4", changes.Single(c => c.Kind == ChangeKind.Insert).Key["Id"]);
        Assert.Equal("3", changes.Single(c => c.Kind == ChangeKind.Delete).Key["Id"]);
    }

    [Fact]
    public void Numeric_formatting_differences_are_not_updates()
    {
        var diff = StateDiffer.Diff(Snap("T", ["Id"], Row(("Id", "1"), ("P", "12.50"))), Snap("T", ["Id"], Row(("Id", "1"), ("P", "12.5"))));

        Assert.True(diff.IsEmpty);
    }

    [Fact]
    public void Tables_without_keys_diff_as_multisets_by_row_hash()
    {
        var before = Snap("Log", [], Row(("Msg", "a")), Row(("Msg", "a")), Row(("Msg", "b")));
        var after = Snap("Log", [], Row(("Msg", "a")), Row(("Msg", "b")), Row(("Msg", "c")));

        var t = Assert.Single(StateDiffer.Diff(before, after).Tables);

        Assert.True(t.KeyedByHash);
        Assert.Equal("c", t.Changes.Single(c => c.Kind == ChangeKind.Insert).After!["Msg"]);
        Assert.Equal("a", t.Changes.Single(c => c.Kind == ChangeKind.Delete).Before!["Msg"]);
    }

    [Fact]
    public void Comparer_ignores_generated_columns_and_reports_exact_cells()
    {
        var expected = StateDiffer.Diff(Snap("T", ["Id"]), Snap("T", ["Id"], Row(("Id", "1"), ("Name", "A"), ("At", "2026-11-24 10:00:00"))));
        var actual = StateDiffer.Diff(Snap("T", ["Id"]), Snap("T", ["Id"], Row(("Id", "1"), ("Name", "B"), ("At", "2026-11-24 11:00:00"))));

        var m = Assert.Single(DiffComparer.Compare(expected, actual, ["T.At"]));

        Assert.Equal(("Name", "A", "B"), (m.Column, m.Expected, m.Actual));
        Assert.Equal("Id=1", m.Key);
    }

    [Fact]
    public void Comparer_matches_on_content_when_a_key_column_is_generated()
    {
        var expected = StateDiffer.Diff(Snap("T", ["G"]), Snap("T", ["G"], Row(("G", "aaaa"), ("Name", "A"))));
        var actual = StateDiffer.Diff(Snap("T", ["G"]), Snap("T", ["G"], Row(("G", "bbbb"), ("Name", "A"))));

        Assert.Empty(DiffComparer.Compare(expected, actual, ["T.G"]));
        Assert.Single(DiffComparer.Compare(expected, actual, []), m => m.Actual is null);
    }

    [Fact]
    public void Comparer_reports_missing_and_extra_rows()
    {
        var expected = StateDiffer.Diff(Snap("T", ["Id"]), Snap("T", ["Id"], Row(("Id", "1"), ("N", "a"))));
        var actual = StateDiffer.Diff(Snap("T", ["Id"]), Snap("T", ["Id"], Row(("Id", "2"), ("N", "a"))));

        var mismatches = DiffComparer.Compare(expected, actual, []);

        Assert.Equal(2, mismatches.Count);
        Assert.Contains(mismatches, m => m.Expected == "Insert" && m.Actual is null);
        Assert.Contains(mismatches, m => m.Expected is null && m.Actual == "Insert");
    }

    [Fact]
    public void Canonical_values_compare_across_types()
    {
        Assert.Equal("12.5", HkValue.Canon(12.50m));
        Assert.Equal("12.5", HkValue.Canon(12.5d));
        Assert.Equal("1", HkValue.Canon(true));
        Assert.Equal("2026-11-24 10:00:00", HkValue.Canon(new DateTime(2026, 11, 24, 10, 0, 0)));
        Assert.Equal("2026-11-24 10:00:00.5", HkValue.Canon(new DateTime(2026, 11, 24, 10, 0, 0, 500)));
        Assert.True(HkValue.CanonEquals("2026-11-24T10:00:00", "2026-11-24 10:00:00"));
        Assert.True(HkValue.CanonEquals("7", "7.0"));
        Assert.False(HkValue.CanonEquals(null, ""));
    }

    [Fact]
    public void Fingerprint_changes_with_columns_of_referenced_tables_only()
    {
        var schema = Invoicing();
        var a = schema.Fingerprint(["Customer"]);
        schema.Table("Invoice")!.Columns.Add(new ColumnSchema { Name = "Extra", DataType = "int" });
        var b = schema.Fingerprint(["Customer"]);
        schema.Table("Customer")!.Column("Name")!.MaxLength = 200;
        var c = schema.Fingerprint(["Customer"]);

        Assert.Equal(a, b);
        Assert.NotEqual(b, c);
    }
}
