using Meshmakers.Octo.Runtime.Contracts.StreamData;
using Meshmakers.Octo.Runtime.Engine.CrateDb;

namespace Meshmakers.Octo.Runtime.Engine.UnitTests;

/// <summary>
/// AB#4779: a computed-column formula may be written in the archive's logical column vocabulary —
/// the CK attribute paths the Studio lists — and is translated to the physical names the mXparser
/// evaluation path binds. Before this, only the physical form worked, and nothing on the surface
/// told the user what that form was.
/// </summary>
public class ComputedColumnFormulaRewriterTests
{
    private static CkArchiveColumnSpec Ingested(string path) =>
        new(path, Indexed: false, Required: false);

    private static CkArchiveColumnSpec Computed(string name) =>
        new(string.Empty, Indexed: false, Required: false) { Name = name, Formula = "1" };

    /// <summary>The voestalpine archive's shape: a dotted path, a plain one, and a formula column.</summary>
    private static readonly IReadOnlyList<CkArchiveColumnSpec> Columns =
    [
        Ingested("Amount.Value"),
        Ingested("Amount.Unit"),
        Ingested("ObisCode"),
        Ingested("DataQuality"),
        Computed("Power")
    ];

    private static string Rewrite(string formula) =>
        ComputedColumnFormulaRewriter.ToPhysicalForm(formula, Columns);

    [Theory]
    [InlineData("Amount.Value", "amountvalue")]
    [InlineData("Amount.Value / 1000", "amountvalue / 1000")]
    [InlineData("Amount.Value * DataQuality", "amountvalue * dataquality")]
    [InlineData("ObisCode", "obiscode")]
    public void LogicalNames_AreTranslated(string formula, string expected)
    {
        Assert.Equal(expected, Rewrite(formula));
    }

    [Theory]
    [InlineData("amount.value")]
    [InlineData("AMOUNT.VALUE")]
    [InlineData("aMoUnT.vAlUe")]
    public void Matching_IsCaseInsensitive(string formula)
    {
        // Same rule as StreamDataFieldResolver on the query side.
        Assert.Equal("amountvalue", Rewrite(formula));
    }

    [Fact]
    public void PhysicalNames_PassThroughUnchanged()
    {
        // Backwards compatibility, and it costs nothing: a physical name is not a CK path, so it
        // matches nothing in the map and is left alone — then resolves downstream as it always did.
        Assert.Equal("amountvalue / 1000", Rewrite("amountvalue / 1000"));
    }

    [Fact]
    public void ComputedColumn_IsAddressableByItsName()
    {
        Assert.Equal("power * 2", Rewrite("Power * 2"));
    }

    [Fact]
    public void UnknownName_IsLeftAsWritten()
    {
        // So the validator can reject it by the spelling the caller used. A half-rewritten formula
        // would produce a confusing message about a name nobody typed.
        Assert.Equal("Temparatur + 1", Rewrite("Temparatur + 1"));
    }

    [Fact]
    public void PartialMatch_DoesNotRewrite()
    {
        // "Amount" alone is not a column here. Rewriting the prefix would yield "amount.Foo" —
        // broken output instead of an honest unknown-column error.
        Assert.Equal("Amount.Foo", Rewrite("Amount.Foo"));
    }

    [Fact]
    public void LongerNameWins_WhenAPrefixIsAlsoAColumn()
    {
        var columns = new[] { Ingested("Amount"), Ingested("Amount.Value") };

        Assert.Equal("amountvalue", ComputedColumnFormulaRewriter.ToPhysicalForm("Amount.Value", columns));
        Assert.Equal("amount", ComputedColumnFormulaRewriter.ToPhysicalForm("Amount", columns));
    }

    [Theory]
    [InlineData("1.5")]
    [InlineData("Amount.Value * 1.5")]
    [InlineData("1.5e3")]
    [InlineData("1.5E-3")]
    public void NumericLiterals_AreNeverTouched(string formula)
    {
        // The dot in a number must not be read as a path separator, and the exponent's 'e' must not
        // start an identifier run — otherwise a column named "e3" would land inside a number.
        var rewritten = Rewrite(formula);
        Assert.Contains(formula.Replace("Amount.Value", "amountvalue"), rewritten);
    }

    [Theory]
    [InlineData("startOfDay(1)", "startOfDay(1)")]
    [InlineData("now(0)", "now(0)")]
    [InlineData("if(DataQuality > 0, Amount.Value, null)", "if(dataquality > 0, amountvalue, null)")]
    public void FunctionNames_AreLeftAlone(string formula, string expected)
    {
        // Asserted on the whole result rather than on substrings: that is what shows the column names
        // changed and *nothing else* did — no shifted parenthesis, no mangled function name. A
        // substring check here would pass while the rewriter corrupted the call around it.
        Assert.Equal(expected, Rewrite(formula));
    }

    [Fact]
    public void Whitespace_AndOperators_SurviveVerbatim()
    {
        Assert.Equal("( amountvalue  +\tdataquality )\n* 2",
            Rewrite("( Amount.Value  +\tDataQuality )\n* 2"));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void BlankFormula_IsReturnedUnchanged(string? formula)
    {
        Assert.Equal(formula ?? string.Empty,
            ComputedColumnFormulaRewriter.ToPhysicalForm(formula, Columns));
    }

    [Fact]
    public void TrailingDot_IsNotSwallowed()
    {
        // Malformed input, but the rewriter must not eat characters it does not understand — the
        // formula engine gets to report the syntax error.
        Assert.Equal("amountvalue.", Rewrite("Amount.Value."));
    }
}
