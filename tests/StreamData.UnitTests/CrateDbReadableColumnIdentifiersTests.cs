using System.Linq;
using Meshmakers.Octo.ConstructionKit.Contracts;
using Meshmakers.Octo.Runtime.Contracts.Formulas;
using Meshmakers.Octo.Runtime.Contracts.StreamData;
using Meshmakers.Octo.Runtime.Engine.CrateDb;
using Xunit;

namespace Meshmakers.Octo.Runtime.Engine.UnitTests;

/// <summary>
/// Read-path projection gating (AB#4189 Phase 7, §8.2 D-7.2): the field resolver that decides which
/// columns a query may project must register a computed column by its <c>Name</c> (closing the MVP
/// gap where computed columns were fed by their empty <c>Path</c> and so were never queryable), but
/// only when the column is readable — a column mid-backfill / failed stays hidden so consumers keep
/// seeing the previous archive state until the swap commits.
/// </summary>
public class CrateDbReadableColumnIdentifiersTests
{
    private static readonly OctoObjectId Archive = OctoObjectId.GenerateNewId();
    private static readonly RtCkId<CkTypeId> SomeType = new("Test", new CkTypeId("EnergyMeter"));

    private static CkArchiveColumnSpec Ing(string path) => new(path, Indexed: true, Required: false);

    private static CkArchiveColumnSpec Comp(string name, ComputedColumnState? state) =>
        new(string.Empty, Indexed: true, Required: false)
        {
            Name = name,
            Formula = "activepower / apparentpower",
            ResultType = FormulaResultType.Double,
            ComputedState = state,
        };

    private static string[] Resolve(params CkArchiveColumnSpec[] columns) =>
        CrateDbStreamDataRepository.ReadableColumnIdentifiers(
                new ArchiveSnapshot(Archive, SomeType, CkArchiveStatus.Activated, "energy", columns))
            .ToArray();

    [Fact]
    public void IngestedColumns_ContributeTheirPath()
    {
        Assert.Equal(new[] { "ActivePower", "ApparentPower" },
            Resolve(Ing("ActivePower"), Ing("ApparentPower")));
    }

    [Fact]
    public void ComputedColumn_CreatedWithArchive_NullState_IsReadableByName()
    {
        // A computed column created together with its archive has no lifecycle state yet → live.
        Assert.Equal(new[] { "powerFactor" }, Resolve(Comp("powerFactor", state: null)));
    }

    [Fact]
    public void ComputedColumn_Active_IsReadableByName()
    {
        Assert.Equal(new[] { "powerFactor" },
            Resolve(Comp("powerFactor", ComputedColumnState.Active)));
    }

    [Theory]
    [InlineData(ComputedColumnState.Pending)]
    [InlineData(ComputedColumnState.Backfilling)]
    [InlineData(ComputedColumnState.Failed)]
    public void ComputedColumn_MidBackfillOrFailed_IsHidden(ComputedColumnState state)
    {
        Assert.Empty(Resolve(Comp("powerFactor", state)));
    }

    [Fact]
    public void ComputedColumn_MissingName_IsSkippedDefensively()
    {
        Assert.Empty(Resolve(Comp(string.Empty, ComputedColumnState.Active)));
    }

    [Fact]
    public void MixedColumns_OnlyIngestedAndReadableComputedAreProjected()
    {
        var result = Resolve(
            Ing("ActivePower"),
            Comp("powerFactor", ComputedColumnState.Active),
            Comp("draftRatio", ComputedColumnState.Backfilling),
            Comp("legacyRatio", state: null));

        Assert.Equal(new[] { "ActivePower", "powerFactor", "legacyRatio" }, result);
    }
}
