using System.Linq;
using Meshmakers.Octo.ConstructionKit.Contracts;
using Meshmakers.Octo.Runtime.Contracts.Formulas;
using Meshmakers.Octo.Runtime.Contracts.StreamData;
using Meshmakers.Octo.Runtime.Engine.CrateDb;
using Xunit;

namespace Meshmakers.Octo.Runtime.Engine.UnitTests;

/// <summary>
/// Read-path projection gating + versioned physical naming (AB#4189 Phase 7, §8): a computed column
/// is projected by its logical <c>Name</c> mapped to its <em>active versioned</em> physical column
/// (<c>{base}__v{N}</c>); columns mid-backfill / failed stay hidden so consumers keep the previous
/// state until the swap.
/// </summary>
public class CrateDbReadableColumnIdentifiersTests
{
    private static readonly OctoObjectId Archive = OctoObjectId.GenerateNewId();
    private static readonly RtCkId<CkTypeId> SomeType = new("Test", new CkTypeId("EnergyMeter"));

    private static CkArchiveColumnSpec Ing(string path) => new(path, Indexed: true, Required: false);

    private static CkArchiveColumnSpec Comp(string name, ComputedColumnState? state, int version = 0) =>
        new(string.Empty, Indexed: true, Required: false)
        {
            Name = name,
            Formula = "activepower / apparentpower",
            ResultType = FormulaResultType.Double,
            ComputedState = state,
            ComputedVersion = version,
        };

    private static (string Name, string Physical)[] Readable(params CkArchiveColumnSpec[] columns) =>
        CrateDbStreamDataRepository.ReadableComputedColumns(
                new ArchiveSnapshot(Archive, SomeType, CkArchiveStatus.Activated, "energy", columns))
            .ToArray();

    [Fact]
    public void Naming_Version0_IsBaseName()
    {
        var c = Comp("powerFactor", ComputedColumnState.Active, version: 0);
        Assert.Equal("powerfactor", ComputedColumnNaming.Base(c));
        Assert.Equal("powerfactor", ComputedColumnNaming.Active(c));
        Assert.Equal("powerfactor__v1", ComputedColumnNaming.Pending(c));
    }

    [Fact]
    public void Naming_VersionN_IsSuffixed()
    {
        var c = Comp("powerFactor", ComputedColumnState.Active, version: 2);
        Assert.Equal("powerfactor", ComputedColumnNaming.Base(c));
        Assert.Equal("powerfactor__v2", ComputedColumnNaming.Active(c));
        Assert.Equal("powerfactor__v3", ComputedColumnNaming.Pending(c));
    }

    [Fact]
    public void IngestedColumns_AreNotComputed_NotInResult()
    {
        Assert.Empty(Readable(Ing("ActivePower"), Ing("ApparentPower")));
    }

    [Fact]
    public void ComputedColumn_NullState_ReadableAtBaseName()
    {
        Assert.Equal(new[] { ("powerFactor", "powerfactor") }, Readable(Comp("powerFactor", state: null)));
    }

    [Fact]
    public void ComputedColumn_Active_Version2_ReadableAtVersionedName()
    {
        Assert.Equal(new[] { ("powerFactor", "powerfactor__v2") },
            Readable(Comp("powerFactor", ComputedColumnState.Active, version: 2)));
    }

    [Theory]
    [InlineData(ComputedColumnState.Pending)]
    [InlineData(ComputedColumnState.Backfilling)]
    [InlineData(ComputedColumnState.Failed)]
    public void ComputedColumn_MidBackfillOrFailed_IsHidden(ComputedColumnState state)
    {
        Assert.Empty(Readable(Comp("powerFactor", state)));
    }

    [Fact]
    public void ComputedColumn_MissingName_IsSkippedDefensively()
    {
        Assert.Empty(Readable(Comp(string.Empty, ComputedColumnState.Active)));
    }

    [Fact]
    public void MixedColumns_OnlyReadableComputedProjected_WithVersionedNames()
    {
        var result = Readable(
            Ing("ActivePower"),
            Comp("powerFactor", ComputedColumnState.Active, version: 1),
            Comp("draftRatio", ComputedColumnState.Backfilling),
            Comp("legacyRatio", state: null));

        Assert.Equal(new[] { ("powerFactor", "powerfactor__v1"), ("legacyRatio", "legacyratio") }, result);
    }
}
