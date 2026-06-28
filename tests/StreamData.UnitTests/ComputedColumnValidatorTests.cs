using System.Collections.Generic;
using Meshmakers.Octo.ConstructionKit.Contracts;
using Meshmakers.Octo.Runtime.Contracts.Formulas;
using Meshmakers.Octo.Runtime.Contracts.StreamData;
using Meshmakers.Octo.Runtime.Engine.CrateDb;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Meshmakers.Octo.Runtime.Engine.UnitTests;

public class ComputedColumnValidatorTests
{
    private static readonly OctoObjectId Archive = OctoObjectId.GenerateNewId();

    private static readonly IFormulaEngine Formula =
        new ServiceCollection().AddFormulaEngine().BuildServiceProvider().GetRequiredService<IFormulaEngine>();

    private static CkArchiveColumnSpec Ing(string path) => new(path, Indexed: true, Required: false);

    private static CkArchiveColumnSpec Comp(string name, string formula,
        FormulaResultType? rt = FormulaResultType.Double, bool required = false, string path = "") =>
        new(path, Indexed: true, Required: required) { Name = name, Formula = formula, ResultType = rt };

    private static void Validate(params CkArchiveColumnSpec[] columns) =>
        ComputedColumnValidator.Validate(Archive, columns, Formula);

    [Fact]
    public void ValidComputedColumn_DoesNotThrow()
    {
        Validate(Ing("activePower"), Ing("apparentPower"),
            Comp("powerFactor", "activepower / apparentpower"));
    }

    [Fact]
    public void NoComputedColumns_DoesNotThrow()
    {
        Validate(Ing("voltage"), Ing("current"));
    }

    [Fact]
    public void ChainedComputed_ReferencingEarlierComputed_DoesNotThrow()
    {
        Validate(Ing("a"), Comp("c1", "a + 1"), Comp("c2", "c1 * 2"));
    }

    [Fact]
    public void PathAndFormula_Throws()
    {
        var ex = Assert.Throws<ComputedColumnInvalidException>(() =>
            Validate(Comp("x", "a + 1", path: "somePath")));
        Assert.Contains("Path", ex.Message);
    }

    [Fact]
    public void RequiredComputed_Throws()
    {
        Assert.Throws<ComputedColumnInvalidException>(() =>
            Validate(Ing("a"), Comp("x", "a + 1", required: true)));
    }

    [Fact]
    public void MissingName_Throws()
    {
        var spec = new CkArchiveColumnSpec(string.Empty, Indexed: true, Required: false)
        {
            Formula = "a + 1",
            ResultType = FormulaResultType.Double,
        };
        Assert.Throws<ComputedColumnInvalidException>(() => Validate(Ing("a"), spec));
    }

    [Fact]
    public void MissingResultType_Throws()
    {
        Assert.Throws<ComputedColumnInvalidException>(() =>
            Validate(Ing("a"), Comp("x", "a + 1", rt: null)));
    }

    [Fact]
    public void UnknownReference_Throws()
    {
        // 'missing' is not a column → CheckSyntax fails.
        Assert.Throws<ComputedColumnInvalidException>(() =>
            Validate(Ing("activePower"), Comp("x", "activepower / missing")));
    }

    [Fact]
    public void SyntaxError_Throws()
    {
        Assert.Throws<ComputedColumnInvalidException>(() =>
            Validate(Ing("a"), Comp("x", "a + + ) (")));
    }

    [Fact]
    public void CyclicReference_Throws()
    {
        // c1 references c2 and c2 references c1.
        Assert.Throws<ComputedColumnInvalidException>(() =>
            Validate(Comp("c1", "c2 + 1"), Comp("c2", "c1 + 1")));
    }

    [Fact]
    public void DivisionByZeroAtProbeValues_DoesNotThrow()
    {
        // CheckSyntax does not evaluate, so a formula that divides by zero only at the probe values
        // is still accepted — it is syntactically valid and its references resolve.
        Validate(Ing("a"), Ing("b"), Comp("x", "a / (b - b)"));
    }
}
