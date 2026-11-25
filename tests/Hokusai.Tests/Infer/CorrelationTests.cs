using Hokusai.Core;
using Hokusai.Infer;
using Hokusai.Runtime;

namespace Hokusai.Tests.Infer;

public sealed class CorrelationTests
{
    private static readonly string[] Cultures = ["de-DE", "en-US"];

    private static List<string> Signatures(string? ui, string? literal, UiEventKind kind = UiEventKind.ValueChanged) =>
        [.. ValueCorrelator.Transforms(ui, literal, kind, Cultures).Select(t => t.Signature)];

    [Theory]
    [InlineData("Acme", "Acme", "identity")]
    [InlineData("  Acme ", "Acme", "trim")]
    [InlineData("acme", "ACME", "upper")]
    [InlineData("ACME", "acme", "lower")]
    [InlineData("Ac", "%Ac%", "like:contains")]
    [InlineData("Ac", "Ac%", "like:starts")]
    [InlineData("me", "%me", "like:ends")]
    public void Text_transforms(string ui, string literal, string expected) => Assert.Contains(expected, Signatures(ui, literal));

    [Fact]
    public void Locale_aware_numbers_record_the_culture()
    {
        Assert.Contains("number:de-DE", Signatures("1.234,50", "1234.5"));
        Assert.Contains("number:en-US", Signatures("1,234.50", "1234.5"));
        Assert.DoesNotContain("number:en-US", Signatures("1.234,50", "1234.5"));
    }

    [Fact]
    public void Currency_symbols_are_ignored_when_parsing_numbers() => Assert.Contains("number:de-DE", Signatures("12,50 €", "12.5"));

    [Fact]
    public void Locale_aware_dates_map_to_the_literal_format()
    {
        Assert.Contains("date:de-DE:yyyy-MM-dd", Signatures("24.11.2026", "2026-11-24"));
        Assert.Contains("date:en-US:yyyyMMdd", Signatures("11/24/2026", "20261124"));
    }

    [Theory]
    [InlineData("On", "1", "bool:1|0")]
    [InlineData("On", "-1", "bool:-1|0")]
    [InlineData("On", "Y", "bool:Y|N")]
    [InlineData("Off", "F", "bool:T|F")]
    [InlineData("On", "True", "bool:True|False")]
    [InlineData("Off", "false", "bool:true|false")]
    public void Boolean_encodings(string ui, string literal, string expected) => Assert.Contains(expected, Signatures(ui, literal, UiEventKind.Toggled));

    [Fact]
    public void Unchecked_zero_is_ambiguous_between_vb_and_c_style_true()
    {
        var s = Signatures("Off", "0", UiEventKind.Toggled);

        Assert.Contains("bool:1|0", s);
        Assert.Contains("bool:-1|0", s);
    }

    [Fact]
    public void Empty_text_stored_as_null_is_recognized() => Assert.Contains("empty-null", Signatures("", null));

    [Fact]
    public void Unrelated_values_do_not_match() => Assert.Empty(Signatures("Acme", "Globex"));

    [Fact]
    public void Trivially_true_transforms_are_offered_so_demonstrations_agree()
    {
        Assert.Contains("trim", Signatures("Hooli", "Hooli"));
        Assert.Contains("upper", Signatures("ACME", "ACME"));

        Demonstration Demo(int i, string typed, string stored) => new DemoBuilder("c", i).Text("txtName", "Name:", typed).Click("btnSave", "Save")
            .Sql($"INSERT INTO Customer (Name, City, CreditLimit) VALUES (N'{stored}', NULL, 0)").Build();
        var t = TemplateInferrer.Infer("c", [Demo(1, "Hooli", "Hooli"), Demo(2, "  Umbrella  ", "Umbrella")], new InferenceContext { Schema = TestSchema.Invoicing() });

        Assert.Empty(t.Issues);
        Assert.Equal(EncodingKind.Trim, t.AllStatements().Single().Bindings[0].Encoding.Kind);
    }

    [Fact]
    public void Encodings_reproduce_the_literal()
    {
        Assert.Equal("ACME", HkEncode.Encode(ValueEncoding.Of(EncodingKind.Upper), "acme"));
        Assert.Equal("%Ac%", HkEncode.Encode(ValueEncoding.Of(EncodingKind.LikeContains), "Ac"));
        Assert.Equal(-1, HkEncode.Encode(ValueEncoding.Of(EncodingKind.BoolMap, "-1|0"), true));
        Assert.Equal("N", HkEncode.Encode(ValueEncoding.Of(EncodingKind.BoolMap, "Y|N"), false));
        Assert.Equal("2026-11-24", HkEncode.Encode(ValueEncoding.Of(EncodingKind.DateText, "yyyy-MM-dd"), new DateOnly(2026, 11, 24)));
        Assert.Equal(1234.5m, HkParse.FromUi("1.234,50", "decimal", "de-DE", null));
        Assert.Equal(new DateTime(2026, 11, 24), HkParse.FromUi("24.11.2026", "DateTime", "de-DE", null));
    }

    [Fact]
    public void Timestamps_near_the_demonstration_are_generated()
    {
        var demo = new DemoBuilder("x", 1).Build();
        var local = demo.Start.LocalDateTime.AddSeconds(2);

        var now = ValueCorrelator.Generated(local.ToString("yyyy-MM-dd HH:mm:ss.fff", System.Globalization.CultureInfo.InvariantCulture), demo);
        var utc = ValueCorrelator.Generated(demo.Start.UtcDateTime.AddSeconds(1).ToString("yyyy-MM-ddTHH:mm:ss", System.Globalization.CultureInfo.InvariantCulture), demo);
        var old = ValueCorrelator.Generated("2001-01-01 10:00:00", demo);

        Assert.Equal("now", now?.Generator);
        Assert.Equal("yyyy-MM-dd HH:mm:ss.fff", now?.Encoding.Format);
        Assert.NotNull(utc);
        Assert.Null(old);
    }

    [Fact]
    public void Guids_and_todays_date_are_generated()
    {
        var demo = new DemoBuilder("x", 1).Build();

        Assert.Equal("guid", ValueCorrelator.Generated("3F2504E0-4F89-11D3-9A0C-0305E82C3301", demo)?.Generator);
        Assert.Equal(EncodingKind.Upper, ValueCorrelator.Generated("3F2504E0-4F89-11D3-9A0C-0305E82C3301", demo)?.Encoding.Kind);
        Assert.Equal("today", ValueCorrelator.Generated(demo.Start.LocalDateTime.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture), demo)?.Generator);
    }
}
