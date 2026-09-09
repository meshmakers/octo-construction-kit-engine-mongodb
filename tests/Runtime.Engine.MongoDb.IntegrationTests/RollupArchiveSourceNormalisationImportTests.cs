using FluentAssertions;

using Meshmakers.Octo.ConstructionKit.Contracts;
using Meshmakers.Octo.Runtime.Contracts.StreamData;
using Meshmakers.Octo.Runtime.Engine.MongoDb.IntegrationTests.Collections;
using Meshmakers.Octo.Runtime.Engine.MongoDb.IntegrationTests.Fixtures;

using Xunit;

namespace Meshmakers.Octo.Runtime.Engine.MongoDb.IntegrationTests;

/// <summary>
///     Seed compatibility of the multi-source rollup model (AB#5157, System.StreamData 1.8.0): the
///     rollup entities of the shipped seeds — the EnergyCommunity.Base blueprint and the energy
///     simulator sample, both written against 1.7.x with the single <c>SourceArchiveRtId</c> scalar —
///     still import through <c>ImportRt</c> and read back as exactly one unbounded source, while a
///     new-form seed declaring <c>Sources</c> only reads back as the declared list. Both forms are
///     counted alike by the source guard.
/// </summary>
/// <remarks>
///     The seeds are copied verbatim into <c>testData/rollupSources</c> and trimmed to their
///     RollupArchive entities (see the header comment of each file): the archives they point at are
///     referenced through a plain string attribute, not an association, so they need not exist, and
///     leaving them out keeps the fixture on the System.StreamData model alone.
/// </remarks>
[Collection(RollupArchiveSourceSeedCollection.Name)]
public class RollupArchiveSourceNormalisationImportTests(RollupArchiveSourceSeedFixture fixture)
{
    private static readonly DateTime Cutover = new(2025, 1, 1, 0, 0, 0, DateTimeKind.Utc);

    /// <summary>The EnergyCommunity raw 15-min archive — source of the hourly rollup and of the new-form seed.</summary>
    private static readonly OctoObjectId EnergyCommunityRawArchive = new("ec0000000000000000000a01");

    /// <summary>The EnergyCommunity legacy daily archive — source of the legacy monthly rollup and of the new-form seed.</summary>
    private static readonly OctoObjectId EnergyCommunityLegacyDailyArchive = new("ec0000000000000000000a11");

    /// <summary>The EnergyCommunity daily rollup — source of both the weekly and the monthly rollup.</summary>
    private static readonly OctoObjectId EnergyCommunityDailyRollup = new("ec0000000000000000000a03");

    /// <summary>The simulator quarter-hour archive — source of the simulator hourly rollup only.</summary>
    private static readonly OctoObjectId SimulatorQuarterHourArchive = new("ab0000000000000000000015");

    /// <summary>The new-form rollup declaring two time-disjoint sources through <c>Sources</c>.</summary>
    private static readonly OctoObjectId MultiSourceRollup = new("ec0000000000000000005157");

    [Fact]
    public void ShippedSeeds_WrittenAgainstTheSingleScalar_StillImport()
    {
        // A 1.8.0 tenant must keep accepting the seeds shipped for 1.7.x. The scalar attribute is
        // optional now, the Sources record array is new — neither may make ImportRt reject an entity
        // (ExchangeException.AttributeNotFound) or trip the mandatory-attribute validator.
        fixture.ImportException.Should().BeNull(
            "the shipped 1.7.x rollup seeds must still import into a System.StreamData 1.8.0 tenant");
    }

    [Theory]
    // EnergyCommunity.Base blueprint ladder: hourly → daily → weekly / monthly → yearly.
    [InlineData("ec0000000000000000000a02", "ec0000000000000000000a01")]
    [InlineData("ec0000000000000000000a03", "ec0000000000000000000a02")]
    [InlineData("ec0000000000000000000a04", "ec0000000000000000000a03")]
    [InlineData("ec0000000000000000000a05", "ec0000000000000000000a03")]
    [InlineData("ec0000000000000000000a06", "ec0000000000000000000a05")]
    // ... plus its legacy sub-ladder over the legacy daily TimeRangeArchive.
    [InlineData("ec0000000000000000000a12", "ec0000000000000000000a11")]
    [InlineData("ec0000000000000000000a13", "ec0000000000000000000a12")]
    // Energy simulator sample (unversioned CK ids): hourly → daily → monthly.
    [InlineData("ab00000000000000000001a0", "ab0000000000000000000015")]
    [InlineData("ab00000000000000000001b0", "ab00000000000000000001a0")]
    [InlineData("ab00000000000000000001c0", "ab00000000000000000001b0")]
    public async Task ASeededScalarSource_ReadsBackAsExactlyOneUnboundedSource(string rollupRtId, string sourceRtId)
    {
        var snapshot = await GetRollupAsync(new OctoObjectId(rollupRtId));

        snapshot.Sources.Should().ContainSingle()
            .Which.Should().BeEquivalentTo(new RollupSourceReference(new OctoObjectId(sourceRtId)));
        snapshot.Sources[0].IsUnbounded.Should().BeTrue("a seeded scalar carries no validity span");
        snapshot.SingleUnboundedSourceRtId.Should().Be(new OctoObjectId(sourceRtId),
            "the deprecated read surfaces keep answering for a normalised single-scalar rollup");
        snapshot.ConflictingSourceArchiveRtId.Should().BeNull(
            "the seed declares the scalar alone, so there is nothing to conflict with");
    }

    [Fact]
    public async Task ANewFormSeed_ReadsBackAsTheDeclaredSourceList()
    {
        var snapshot = await GetRollupAsync(MultiSourceRollup);

        snapshot.Sources.Should().BeEquivalentTo(
            new[]
            {
                new RollupSourceReference(EnergyCommunityLegacyDailyArchive, ValidTo: Cutover),
                new RollupSourceReference(EnergyCommunityRawArchive, ValidFrom: Cutover),
            },
            options => options.WithStrictOrdering(),
            "the declaration order of the seed is preserved");
        snapshot.SingleUnboundedSourceRtId.Should().BeNull("neither source is unbounded");
        snapshot.ConflictingSourceArchiveRtId.Should().BeNull("no deprecated scalar is seeded alongside");

        // Half-open spans: the cutover bucket already belongs to the natively ingested source.
        snapshot.SourceForBucket(new DateTime(2024, 12, 1, 0, 0, 0, DateTimeKind.Utc), Cutover)!
            .SourceArchiveRtId.Should().Be(EnergyCommunityLegacyDailyArchive);
        snapshot.SourceForBucket(Cutover, new DateTime(2025, 2, 1, 0, 0, 0, DateTimeKind.Utc))!
            .SourceArchiveRtId.Should().Be(EnergyCommunityRawArchive);
    }

    [Theory]
    // The raw archive is the source of the old-form hourly rollup AND of the new-form one.
    [InlineData("ec0000000000000000000a01", 2)]
    // The legacy daily archive likewise: old-form legacy monthly + the new-form rollup's first span.
    [InlineData("ec0000000000000000000a11", 2)]
    // Two old-form rollups (weekly and monthly) chain on the daily rollup.
    [InlineData("ec0000000000000000000a03", 2)]
    // The simulator's quarter-hour archive feeds one old-form rollup only.
    [InlineData("ab0000000000000000000015", 1)]
    // An archive nothing points at.
    [InlineData("ec00000000000000000000ff", 0)]
    public async Task CountActiveRollupsForSource_CountsBothDeclarationForms(string sourceRtId, int expected)
    {
        var store = fixture.TenantContext.GetRollupArchiveRuntimeStore();
        store.Should().NotBeNull();

        var count = await store!.CountActiveRollupsForSourceAsync(new OctoObjectId(sourceRtId));

        count.Should().Be(expected);
    }

    private async Task<RollupArchiveSnapshot> GetRollupAsync(OctoObjectId rollupRtId)
    {
        fixture.ImportException.Should().BeNull("the seeds must have imported before they can be read back");
        var store = fixture.TenantContext.GetRollupArchiveRuntimeStore();
        store.Should().NotBeNull();

        var snapshot = await store!.GetAsync(rollupRtId);
        snapshot.Should().NotBeNull($"the seed declares a rollup '{rollupRtId}'");
        return snapshot!;
    }
}
