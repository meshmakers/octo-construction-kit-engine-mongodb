using Meshmakers.Octo.ConstructionKit.Contracts;
using Meshmakers.Octo.ConstructionKit.Models.StreamData.Generated.System.StreamData.v1;
using Meshmakers.Octo.Runtime.Contracts.RepositoryEntities;
using Meshmakers.Octo.Runtime.Contracts.StreamData;
using Meshmakers.Octo.Runtime.Engine.MongoDb.StreamData;
using Xunit;

namespace Meshmakers.Octo.Runtime.Engine.UnitTests;

/// <summary>
/// AB#5157: <see cref="MongoRollupArchiveRuntimeStore.NormaliseSources"/> is the single point that
/// folds the two storage forms of a rollup's source declaration — the <c>Sources</c> record list
/// (System.StreamData 1.8.0) and the deprecated <c>SourceArchiveRtId</c> scalar — into the
/// normalised <see cref="RollupArchiveSnapshot.Sources"/> list. These tests pin the rule table and
/// the write-side invariant that <c>InsertAsync</c> never writes the deprecated scalar.
/// </summary>
public class MongoRollupArchiveRuntimeStoreMapToSnapshotTests
{
    private static readonly OctoObjectId LegacyRtId = new("aa00000000000000000002a0");
    private static readonly OctoObjectId NativeRtId = new("aa00000000000000000002b0");
    private static readonly OctoObjectId OtherRtId = new("aa00000000000000000002c0");
    private static readonly DateTime Cutover = new(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);

    [Fact]
    public void OldForm_ScalarOnly_BecomesOneUnboundedSource()
    {
        var entity = new RtRollupArchive { SourceArchiveRtId = LegacyRtId.ToString() };

        var (sources, conflicting) = MongoRollupArchiveRuntimeStore.NormaliseSources(entity);

        var single = Assert.Single(sources);
        Assert.Equal(LegacyRtId, single.SourceArchiveRtId);
        Assert.True(single.IsUnbounded);
        Assert.Null(conflicting);
    }

    [Fact]
    public void NewForm_SourcesOnly_MapsEveryReferenceWithItsSpan()
    {
        var entity = new RtRollupArchive
        {
            Sources = SourceList(
                Reference(LegacyRtId, validTo: Cutover),
                Reference(NativeRtId, validFrom: Cutover)),
        };

        var (sources, conflicting) = MongoRollupArchiveRuntimeStore.NormaliseSources(entity);

        Assert.Equal(2, sources.Count);
        Assert.Equal(LegacyRtId, sources[0].SourceArchiveRtId);
        Assert.Null(sources[0].ValidFrom);
        Assert.Equal(Cutover, sources[0].ValidTo);
        Assert.Equal(NativeRtId, sources[1].SourceArchiveRtId);
        Assert.Equal(Cutover, sources[1].ValidFrom);
        Assert.Null(sources[1].ValidTo);
        Assert.Null(conflicting);
    }

    [Fact]
    public void BothForms_Consistent_ListWinsWithoutConflict()
    {
        var entity = new RtRollupArchive
        {
            SourceArchiveRtId = LegacyRtId.ToString(),
            Sources = SourceList(Reference(LegacyRtId)),
        };

        var (sources, conflicting) = MongoRollupArchiveRuntimeStore.NormaliseSources(entity);

        var single = Assert.Single(sources);
        Assert.Equal(LegacyRtId, single.SourceArchiveRtId);
        Assert.True(single.IsUnbounded);
        Assert.Null(conflicting);
    }

    [Fact]
    public void BothForms_DifferentId_ListKeptAndScalarReportedAsConflict()
    {
        var entity = new RtRollupArchive
        {
            SourceArchiveRtId = OtherRtId.ToString(),
            Sources = SourceList(
                Reference(LegacyRtId, validTo: Cutover),
                Reference(NativeRtId, validFrom: Cutover)),
        };

        var (sources, conflicting) = MongoRollupArchiveRuntimeStore.NormaliseSources(entity);

        Assert.Equal(new[] { LegacyRtId, NativeRtId }, sources.Select(s => s.SourceArchiveRtId));
        Assert.Equal(OtherRtId, conflicting);
    }

    [Fact]
    public void BothForms_SameIdButBoundedSpan_IsAConflict()
    {
        // The scalar means "this archive, for every bucket"; a bounded reference to the same
        // archive says something else — the list is kept, the scalar flagged.
        var entity = new RtRollupArchive
        {
            SourceArchiveRtId = LegacyRtId.ToString(),
            Sources = SourceList(Reference(LegacyRtId, validTo: Cutover)),
        };

        var (sources, conflicting) = MongoRollupArchiveRuntimeStore.NormaliseSources(entity);

        var single = Assert.Single(sources);
        Assert.Equal(Cutover, single.ValidTo);
        Assert.Equal(LegacyRtId, conflicting);
    }

    [Fact]
    public void NeitherForm_EmptyListWithoutConflict()
    {
        // ImportRt seeds may lack both attributes entirely — the generated getters would throw,
        // the normaliser must not.
        var entity = new RtRollupArchive();

        var (sources, conflicting) = MongoRollupArchiveRuntimeStore.NormaliseSources(entity);

        Assert.Empty(sources);
        Assert.Null(conflicting);
    }

    [Fact]
    public void MalformedIds_AreSkippedNotThrown()
    {
        var entity = new RtRollupArchive
        {
            SourceArchiveRtId = "not-an-object-id",
            Sources = SourceList(
                new RtCkRollupSourceReferenceRecord { SourceArchiveRtId = "also-not-an-id" },
                Reference(NativeRtId)),
        };

        var (sources, conflicting) = MongoRollupArchiveRuntimeStore.NormaliseSources(entity);

        var single = Assert.Single(sources);
        Assert.Equal(NativeRtId, single.SourceArchiveRtId);
        Assert.Null(conflicting);
    }

    [Fact]
    public void InsertEntity_WritesSourcesOnly_LeavesDeprecatedScalarUnset()
    {
        var declared = new[]
        {
            new RollupSourceReference(LegacyRtId, ValidTo: Cutover),
            new RollupSourceReference(NativeRtId, ValidFrom: Cutover),
        };

        var entity = MongoRollupArchiveRuntimeStore.BuildInsertEntity(
            rtWellKnownName: "hourly",
            targetCkTypeId: new RtCkId<CkTypeId>("Demo/EnergyMeasurement"),
            sources: declared,
            bucketSize: TimeSpan.FromHours(1),
            watermarkLag: TimeSpan.FromMinutes(5),
            aggregations: new[] { new CkRollupAggregationSpec("amountValue", CkRollupFunction.Sum, null) },
            columns: Array.Empty<CkArchiveColumnSpec>(),
            bucketAlignment: BucketAlignment.FixedSize,
            referenceTimeZone: null,
            carryLookback: null);

        // The platform never writes the deprecated scalar — not even as null.
        Assert.False(entity.Attributes.ContainsKey("SourceArchiveRtId"));
        Assert.Null(entity.GetAttributeStringValueOrDefault("SourceArchiveRtId"));

        // Round trip through the normaliser yields exactly the declared list, no conflict.
        var (sources, conflicting) = MongoRollupArchiveRuntimeStore.NormaliseSources(entity);
        Assert.Equal(declared, sources);
        Assert.Null(conflicting);
    }

    private static RtCkRollupSourceReferenceRecord Reference(
        OctoObjectId rtId, DateTime? validFrom = null, DateTime? validTo = null)
        => new()
        {
            SourceArchiveRtId = rtId.ToString(),
            ValidFrom = validFrom,
            ValidTo = validTo,
        };

    private static AttributeRecordValueList<RtCkRollupSourceReferenceRecord> SourceList(
        params RtCkRollupSourceReferenceRecord[] records)
    {
        var list = new AttributeRecordValueList<RtCkRollupSourceReferenceRecord>();
        list.AddRange(records);
        return list;
    }
}
