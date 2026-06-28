using FakeItEasy;
using Meshmakers.Octo.ConstructionKit.Contracts;
using Meshmakers.Octo.ConstructionKit.Contracts.Services;
using Meshmakers.Octo.Runtime.Contracts.Formulas;
using Meshmakers.Octo.Runtime.Contracts.StreamData;
using Meshmakers.Octo.Runtime.Engine.CrateDb;
using Xunit;

namespace Meshmakers.Octo.Runtime.Engine.UnitTests;

public class ArchivePathTypeResolverComputedColumnTests
{
    private readonly ICkCacheService _cache = A.Fake<ICkCacheService>();
    private static readonly RtCkId<CkTypeId> TargetType = new("Industry.Energy/EnergyMeter");

    private static CkArchiveColumnSpec ComputedSpec(string name, string formula, FormulaResultType type) =>
        new(string.Empty, Indexed: true, Required: false)
        {
            Name = name,
            Formula = formula,
            ResultType = type,
        };

    [Theory]
    [InlineData(FormulaResultType.Boolean, "BOOLEAN")]
    [InlineData(FormulaResultType.Int, "INTEGER")]
    [InlineData(FormulaResultType.Int64, "BIGINT")]
    [InlineData(FormulaResultType.Double, "DOUBLE PRECISION")]
    [InlineData(FormulaResultType.DateTime, "TIMESTAMP WITH TIME ZONE")]
    public void Computed_ResolvesResultTypeToCrateType(FormulaResultType resultType, string expectedCrate)
    {
        var result = ArchivePathTypeResolver.Resolve(_cache, "acme", TargetType,
            new[] { ComputedSpec("derived", "a + b", resultType) });

        var col = Assert.Single(result);
        Assert.Equal("derived", col.ColumnName);
        Assert.Equal(expectedCrate, col.Type.Render());
        Assert.False(col.Required); // computed columns are always nullable
    }

    [Fact]
    public void Computed_NameIsLowerCased()
    {
        var result = ArchivePathTypeResolver.Resolve(_cache, "acme", TargetType,
            new[] { ComputedSpec("PowerFactor", "a / b", FormulaResultType.Double) });

        Assert.Equal("powerfactor", Assert.Single(result).ColumnName);
    }

    [Fact]
    public void Computed_OnlyArchive_DoesNotResolveTargetType()
    {
        ArchivePathTypeResolver.Resolve(_cache, "acme", TargetType,
            new[] { ComputedSpec("derived", "a + b", FormulaResultType.Double) });

        A.CallTo(() => _cache.GetRtCkType(A<string>._, A<RtCkId<CkTypeId>>._)).MustNotHaveHappened();
    }

    [Fact]
    public void Computed_MissingResultType_Throws()
    {
        var spec = new CkArchiveColumnSpec(string.Empty, Indexed: true, Required: false)
        {
            Name = "derived",
            Formula = "a + b",
        };

        Assert.Throws<UnresolvableArchivePathException>(() =>
            ArchivePathTypeResolver.Resolve(_cache, "acme", TargetType, new[] { spec }));
    }

    [Fact]
    public void Computed_MissingName_Throws()
    {
        var spec = new CkArchiveColumnSpec(string.Empty, Indexed: true, Required: false)
        {
            Formula = "a + b",
            ResultType = FormulaResultType.Double,
        };

        Assert.Throws<UnresolvableArchivePathException>(() =>
            ArchivePathTypeResolver.Resolve(_cache, "acme", TargetType, new[] { spec }));
    }
}
