using System;
using System.Collections.Generic;
using System.Linq;
using FakeItEasy;
using Meshmakers.Octo.ConstructionKit.Contracts;
using Meshmakers.Octo.ConstructionKit.Contracts.DataTransferObjects;
using Meshmakers.Octo.ConstructionKit.Contracts.DependencyGraph;
using Meshmakers.Octo.ConstructionKit.Contracts.Services;
using Meshmakers.Octo.Runtime.Contracts.Formulas;
using Meshmakers.Octo.Runtime.Contracts.StreamData;
using Meshmakers.Octo.Runtime.Engine.CrateDb;
using Meshmakers.Octo.Runtime.Engine.CrateDb.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace Meshmakers.Octo.Runtime.Engine.UnitTests.Copy;

/// <summary>
/// AB#9141: which columns a backup leaves out of an archive whose columns reach a Hidden attribute - the Hidden columns
/// under the current model, the recorded ones, the aggregate columns of a rollup over a Hidden attribute, and the computed
/// columns derived from any of them (every stored version).
/// </summary>
public class HiddenColumnExclusionTests
{
    private static readonly RtCkId<CkTypeId> SensorType = new("Test", new CkTypeId("Sensor"));
    private static readonly OctoObjectId Archive = OctoObjectId.GenerateNewId();

    private static ICkCacheService SensorCache(bool pinHidden)
    {
        var typeId = new CkId<CkTypeId>("Test-1.0.0/Sensor-1");
        CkTypeAttributeGraph Attribute(string name, CkAttributeAccessDto access) => new(
            new CkId<CkAttributeId>($"Test-1.0.0/{name}-1"), name, null, AttributeValueTypesDto.Double, null, null, null,
            null, null, isOptional: true, description: null) { Access = access };
        CkTypeGraph Graph() => new(typeId, isAbstract: false, isFinal: false, isCollectionRoot: true, [], null, null, [], [],
            new[]
            {
                Attribute("Value", CkAttributeAccessDto.ReadWrite),
                Attribute("Pin", pinHidden ? CkAttributeAccessDto.Hidden : CkAttributeAccessDto.ReadWrite)
            }.ToDictionary(a => a.CkAttributeId, a => a),
            [], new CkGraphDirectedAssociations([]), "sensor", enableChangeStreamPreAndPostImages: false);

        var cache = A.Fake<ICkCacheService>();
        A.CallTo(() => cache.GetRtCkType(A<string>._, SensorType)).ReturnsLazily(Graph);
        CkTypeGraph? ignored;
        A.CallTo(() => cache.TryGetRtCkType(A<string>._, SensorType, out ignored))
            .ReturnsLazily(() => true).AssignsOutAndRefParameters(Graph());
        return cache;
    }

    private static CrateDbStreamDataRepository Repository(ICkCacheService cache) => new(
        NullLogger<CrateDbStreamDataRepository>.Instance, cache, A.Fake<IStreamDataDatabaseClient>(),
        A.Fake<IStreamDataDatabaseManagementClient>(),
        Options.Create(new StreamDataConfiguration { ConnectionString = "Host=ignored" }), "tenant-x",
        A.Fake<IArchiveRuntimeStore>(), A.Fake<IFormulaEngine>());

    private static ArchiveSnapshot Raw(CkArchiveStatus status, params CkArchiveColumnSpec[] extra) =>
        new(Archive, SensorType, status, "meter",
            new List<CkArchiveColumnSpec> { new("Value", false, false), new("Pin", false, false) }.Concat(extra).ToList());

    private static CkArchiveColumnSpec Computed(string name, string formula) =>
        new(string.Empty, false, false) { Name = name, Formula = formula, ResultType = FormulaResultType.Double };

    [Fact]
    public void AColumnThatReachesHidden_AndTheComputedColumnsOverIt_AreLeftOut_InEveryStoredVersion()
    {
        var snapshot = Raw(CkArchiveStatus.Activated, Computed("pinDoubled", "pin * 2"), Computed("pinQuad", "pindoubled * 2"),
            Computed("valueDoubled", "value * 2"));

        var exclusion = Repository(SensorCache(pinHidden: true)).FindHiddenColumnExclusion(snapshot, includeRecorded: true);

        Assert.NotNull(exclusion);
        Assert.Equal(["Pin", "pinDoubled", "pinQuad"],
            exclusion.Columns.Select(c => c.IsComputed ? c.Name : c.Path).OrderBy(n => n, StringComparer.Ordinal));
        Assert.True(exclusion.Excludes("pin"));
        Assert.True(exclusion.Excludes("pindoubled__v2"));
        Assert.True(exclusion.Excludes("pinquad"));
        Assert.False(exclusion.Excludes("pindoubled__vx"));
        Assert.False(exclusion.Excludes("value"));
        Assert.False(exclusion.Excludes("valuedoubled"));
        Assert.Equal(["Value", "valueDoubled"], exclusion.Apply(snapshot).Columns.Select(c => c.IsComputed ? c.Name : c.Path));
    }

    [Fact]
    public void Apply_DropsTheConflictPrecedenceKeysOnALeftOutColumn_AndKeepsTheOthers()
    {
        var snapshot = Raw(CkArchiveStatus.Disabled) with
        {
            ConflictPrecedence =
            [
                new ArchiveConflictKey(ArchiveConflictKey.OriginKey, ConflictKeyOrder.HigherWins),
                new ArchiveConflictKey("Pin", ConflictKeyOrder.HigherWins),
                new ArchiveConflictKey("Value", ConflictKeyOrder.LowerWins)
            ]
        };

        var applied = Repository(SensorCache(pinHidden: true)).FindHiddenColumnExclusion(snapshot, includeRecorded: false)!
            .Apply(snapshot);

        Assert.Equal([ArchiveConflictKey.OriginKey, "Value"], applied.ConflictPrecedence.Select(k => k.Column));
    }

    [Fact]
    public void NothingHidden_LeavesNothingOut()
    {
        Assert.Null(Repository(SensorCache(pinHidden: false))
            .FindHiddenColumnExclusion(Raw(CkArchiveStatus.Activated), includeRecorded: true));
    }

    [Fact]
    public void RecordedHiddenColumns_CountForABackup_ButNotForProvisioning()
    {
        // The model was changed back: Pin is visible, the archive still records it as Hidden.
        var snapshot = Raw(CkArchiveStatus.Failed) with { HiddenColumns = ["Pin"], StatusReason = "Pin" };
        var repository = Repository(SensorCache(pinHidden: false));

        Assert.True(repository.FindHiddenColumnExclusion(snapshot, includeRecorded: true)!.Excludes("pin"));
        Assert.Null(repository.FindHiddenColumnExclusion(snapshot, includeRecorded: false));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void AModelThatCannotBeRead_LeavesTheRecordedColumnsOut(bool includeRecorded)
    {
        var cache = A.Fake<ICkCacheService>();
        A.CallTo(() => cache.GetRtCkType(A<string>._, A<RtCkId<CkTypeId>>._)).Throws(new InvalidOperationException("no type"));
        var snapshot = Raw(CkArchiveStatus.Failed) with { HiddenColumns = ["Pin"], StatusReason = "Pin" };

        var exclusion = Repository(cache).FindHiddenColumnExclusion(snapshot, includeRecorded);

        Assert.NotNull(exclusion);
        Assert.True(exclusion.Excludes("pin"));
        Assert.False(exclusion.Excludes("value"));
    }

    [Fact]
    public void ARollupOverAHiddenAttribute_LeavesItsAggregateColumnsOut()
    {
        var aggregations = new List<CkRollupAggregationSpec>
        {
            new("Pin", CkRollupFunction.Avg, null), new("Value", CkRollupFunction.Sum, null)
        };
        var snapshot = new ArchiveSnapshot(Archive, SensorType, CkArchiveStatus.Activated, "rollup",
            RollupColumnGenerator.Generate(aggregations).ToList())
        {
            RollupAggregations = aggregations, Period = TimeSpan.FromHours(1)
        };
        var pinColumns = aggregations[..1].SelectMany(RollupColumnGenerator.TargetColumnNamesFor).ToList();
        var valueColumns = aggregations[1..].SelectMany(RollupColumnGenerator.TargetColumnNamesFor).ToList();

        var exclusion = Repository(SensorCache(pinHidden: true)).FindHiddenColumnExclusion(snapshot, includeRecorded: true);

        Assert.NotNull(exclusion);
        Assert.NotEmpty(pinColumns);
        Assert.All(pinColumns, c => Assert.True(exclusion.Excludes(c.ToLowerInvariant()), c));
        Assert.All(valueColumns, c => Assert.False(exclusion.Excludes(c.ToLowerInvariant()), c));
        Assert.False(exclusion.Excludes("window_start"));
    }
}
