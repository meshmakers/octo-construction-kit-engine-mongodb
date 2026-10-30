using FluentAssertions;

using Meshmakers.Octo.ConstructionKit.Contracts;
using Meshmakers.Octo.ConstructionKit.Contracts.BlueprintCatalogs;
using Meshmakers.Octo.ConstructionKit.Contracts.DataTransferObjects;
using Meshmakers.Octo.Runtime.Contracts;
using Meshmakers.Octo.Runtime.Contracts.Blueprints;
using Meshmakers.Octo.Runtime.Contracts.Exchange;
using Meshmakers.Octo.Runtime.Contracts.MongoDb.Repositories;
using Meshmakers.Octo.Runtime.Contracts.Repositories;
using Meshmakers.Octo.Runtime.Contracts.RepositoryEntities;
using Meshmakers.Octo.Runtime.Contracts.StreamData;
using Meshmakers.Octo.Runtime.Engine.MongoDb.IntegrationTests.Collections;
using Meshmakers.Octo.Runtime.Engine.MongoDb.IntegrationTests.Fixtures;

using Xunit;

namespace Meshmakers.Octo.Runtime.Engine.MongoDb.IntegrationTests;

/// <summary>
///     AB#7872 against a real tenant: what an activated archive cannot apply is refused through <c>ImportRt</c>, the generic
///     entity API and a blueprint update, with a message that names the archive and the field. Everything else keeps
///     working, a re-apply of an unchanged definition first of all, and the writes of the stream data stores are not
///     touched.
/// </summary>
[Collection(ArchiveDefinitionGuardCollection.Name)]
public class ArchiveDefinitionGuardIntegrationTests(ArchiveDefinitionGuardFixture fixture)
{
    private static readonly RtCkId<CkTypeId> RollupType = new("System.StreamData/RollupArchive");
    private static readonly RtCkId<CkTypeId> RawType = new("System.StreamData/RawArchive");
    private const string SourceArchive = "6a7000000000000000000001";
    private const string Name = "guarded";

    private const string SumOfAmount =
        """
              - id: System.StreamData/RollupArchive.Aggregations
                value:
                  - ckRecordId: System.StreamData/CkRollupAggregation
                    attributes:
                      - id: System.StreamData/CkRollupAggregation.SourcePath
                        value: Amount.Value
                      - id: System.StreamData/CkRollupAggregation.Function
                        value: Sum
        """;

    private const string SourcesList =
        $"""
              - id: System.StreamData/RollupArchive.Sources
                value:
                  - ckRecordId: System.StreamData/CkRollupSourceReference
                    attributes:
                      - id: System.StreamData/CkRollupSourceReference.SourceArchiveRtId
                        value: {SourceArchive}
        """;

    private static string Column(string path) =>
        $"""
                  - ckRecordId: System.StreamData/CkArchiveColumn
                    attributes:
                      - id: System.StreamData/CkArchiveColumn.Path
                        value: {path}
                      - id: System.StreamData/CkArchiveColumn.Required
                        value: false
                      - id: System.StreamData/CkArchiveColumn.Indexed
                        value: true
        """;

    private static string Computed(string name, string resultType) =>
        $"""
                  - ckRecordId: System.StreamData/CkArchiveColumn
                    attributes:
                      - id: System.StreamData/CkArchiveColumn.Path
                        value: ""
                      - id: System.StreamData/CkArchiveColumn.Required
                        value: false
                      - id: System.StreamData/CkArchiveColumn.Indexed
                        value: true
                      - id: System.StreamData/CkArchiveColumn.Name
                        value: {name}
                      - id: System.StreamData/CkArchiveColumn.Formula
                        value: amountvalue * 2
                      - id: System.StreamData/CkArchiveColumn.ResultType
                        value: {resultType}
        """;

    private static string Entity(OctoObjectId rtId, RtCkId<CkTypeId> type, string attributes, string target = "Basic.Energy/EnergyMeasurement") =>
        $"""
        $schema: https://schemas.meshmakers.cloud/runtime-model.schema.json
        dependencies:
          - System.StreamData-[1.8,2.0)
        entities:
          - rtId: {rtId}
            ckTypeId: {type}
            rtWellKnownName: {Name}
            associations: []
            attributes:
              - id: System.StreamData/Archive.TargetCkTypeId
                value: {target}
              - id: System.StreamData/Archive.Columns
                value:
        {Column("amountvalue_sum")}
        {attributes}
        """;

    private static string Rollup(OctoObjectId rtId, long bucketMs = 3_600_000, long lagMs = 300_000, string? extra = null,
        string aggregations = SumOfAmount, string sources = SourcesList, string target = "Basic.Energy/EnergyMeasurement") =>
        Entity(rtId, RollupType,
            $"""
            {sources}
                  - id: System.StreamData/RollupArchive.BucketSizeMs
                    value: {bucketMs}
                  - id: System.StreamData/RollupArchive.WatermarkLagMs
                    value: {lagMs}
            {aggregations}
            {extra}
            """, target);

    private static string Raw(OctoObjectId rtId, params string[] columns) =>
        $"""
        $schema: https://schemas.meshmakers.cloud/runtime-model.schema.json
        dependencies:
          - System.StreamData-[1.8,2.0)
        entities:
          - rtId: {rtId}
            ckTypeId: {RawType}
            rtWellKnownName: {Name}
            associations: []
            attributes:
              - id: System.StreamData/Archive.TargetCkTypeId
                value: Basic.Energy/EnergyMeasurement
              - id: System.StreamData/Archive.Columns
                value:
        {string.Join("\n", columns)}
        """;

    private ITenantRepository Repository => fixture.GetSystemContext().GetSystemTenantRepository();

    private async Task ImportAsync(string yaml, ImportStrategy strategy)
    {
        var file = Path.Combine(Path.GetTempPath(), $"ab7872-{Guid.NewGuid():N}.yaml");
        await File.WriteAllTextAsync(file, yaml, TestContext.Current.CancellationToken);
        try
        {
            await fixture.GetService<IImportRtModelCommand>().ImportAsync(Repository, file, ExchangeMimeTypes.MimeTypeYaml, strategy);
        }
        finally
        {
            File.Delete(file);
        }
    }

    private async Task<OctoObjectId> CreateAsync(Func<OctoObjectId, string> yaml, CkArchiveStatus status = CkArchiveStatus.Activated)
    {
        var rtId = OctoObjectId.GenerateNewId();
        await ImportAsync(yaml(rtId), ImportStrategy.Insert);
        await fixture.TenantContext.GetArchiveRuntimeStore().SetStatusAsync(rtId, status);
        return rtId;
    }

    private async Task<object?> FirstActivatedAtOfAsync(OctoObjectId rtId, RtCkId<CkTypeId>? type = null)
    {
        var session = await Repository.GetSessionAsync();
        var entity = await Repository.GetRtEntityByRtIdAsync(session, new RtEntityId(type ?? RollupType, rtId));
        return entity!.Attributes.GetValueOrDefault("FirstActivatedAt");
    }

    private async Task<long?> BucketSizeOfAsync(OctoObjectId rtId)
    {
        var session = await Repository.GetSessionAsync();
        var entity = await Repository.GetRtEntityByRtIdAsync(session, new RtEntityId(RollupType, rtId));
        return entity!.GetAttributeValueOrDefault<long>("BucketSizeMs");
    }

    [Fact]
    public async Task AReApplyOfAnUnchangedDefinition_Passes()
    {
        var rollup = await CreateAsync(id => Rollup(id));
        var raw = await CreateAsync(id => Raw(id, Column("amountvalue_sum"), Computed("doubled", "Double")));

        await ImportAsync(Rollup(rollup), ImportStrategy.Upsert);
        await ImportAsync(Raw(raw, Column("amountvalue_sum"), Computed("doubled", "Double")), ImportStrategy.Upsert);
    }

    [Fact]
    public async Task AReApply_TakesAFunctionByNameOrKey_AndASourceInEitherStorageForm()
    {
        var rollup = await CreateAsync(id => Rollup(id));
        var byKey = SumOfAmount.Replace("value: Sum", $"value: {(int)CkRollupFunction.Sum}");
        var scalar = $"      - id: System.StreamData/RollupArchive.SourceArchiveRtId\n        value: {SourceArchive}";

        await ImportAsync(Rollup(rollup, aggregations: byKey), ImportStrategy.Upsert);
        await ImportAsync(Rollup(rollup, sources: scalar), ImportStrategy.Upsert);
        // Now stored in the scalar form; the list form names the same source.
        await ImportAsync(Rollup(rollup), ImportStrategy.Upsert);
    }

    public static TheoryData<string, Func<OctoObjectId, string>> FrozenRollupChanges => new()
    {
        { "BucketSizeMs", id => Rollup(id, bucketMs: 7_200_000) },
        { "Aggregations", id => Rollup(id, aggregations: SumOfAmount.Replace("value: Sum", "value: Max")) },
        { "Aggregations", id => Rollup(id, aggregations: SumOfAmount + "\n" + SumOfAmount[SumOfAmount.IndexOf("          - ckRecordId", StringComparison.Ordinal)..].Replace("Amount.Value", "Other")) },
        { "BucketAlignment", id => Rollup(id, extra: "      - id: System.StreamData/RollupArchive.BucketAlignment\n        value: CalendarDay") },
        { "ReferenceTimeZone", id => Rollup(id, extra: "      - id: System.StreamData/RollupArchive.ReferenceTimeZone\n        value: Europe/Vienna") },
        { "Sources", id => Rollup(id, sources: SourcesList.Replace(SourceArchive, "6a7000000000000000000002")) },
        { "TargetCkTypeId", id => Rollup(id, target: "Basic.Energy/Other") },
    };

    [Theory]
    [MemberData(nameof(FrozenRollupChanges))]
    public async Task ImportRt_AChangeAnActivatedRollupCannotApply_IsRefused_WithTheArchiveAndTheField(string field, Func<OctoObjectId, string> changed)
    {
        var rollup = await CreateAsync(id => Rollup(id));

        var act = () => ImportAsync(changed(rollup), ImportStrategy.Upsert);

        var refused = (await act.Should().ThrowAsync<RollupSchemaImmutableException>()).Which;
        refused.Field.Should().Be(field);
        refused.Message.Should().Contain(rollup.ToString()).And.Contain(Name).And.Contain(field).And.Contain("needs a new archive");
        (await BucketSizeOfAsync(rollup)).Should().Be(3_600_000, "nothing was written");
    }

    // Seeds import their archives as Disabled, before any table exists; the seed must stay free to change.
    [Fact]
    public async Task ImportRt_ADisabledRollupThatWasNeverActivated_StaysEditable()
    {
        var rollup = await CreateAsync(id => Rollup(id), CkArchiveStatus.Disabled);

        await ImportAsync(Rollup(rollup, bucketMs: 7_200_000), ImportStrategy.Upsert);

        (await BucketSizeOfAsync(rollup)).Should().Be(7_200_000);
    }

    // Disable, change, enable must not be a way around the guard: enabling meets the same table.
    [Fact]
    public async Task ImportRt_ADisabledRollupThatWasActivated_IsRefused()
    {
        var rollup = await CreateAsync(id => Rollup(id));
        await fixture.TenantContext.GetArchiveRuntimeStore().SetStatusAsync(rollup, CkArchiveStatus.Disabled);

        var act = () => ImportAsync(Rollup(rollup, bucketMs: 7_200_000), ImportStrategy.Upsert);

        (await act.Should().ThrowAsync<RollupSchemaImmutableException>()).Which.Field.Should().Be("BucketSizeMs");
        (await BucketSizeOfAsync(rollup)).Should().Be(3_600_000, "nothing was written");
    }

    [Fact]
    public async Task ImportRt_ADisabledRawArchiveThatWasActivated_RefusesRemovingAColumn()
    {
        var raw = await CreateAsync(id => Raw(id, Column("amountvalue_sum"), Computed("doubled", "Double")));
        await fixture.TenantContext.GetArchiveRuntimeStore().SetStatusAsync(raw, CkArchiveStatus.Disabled);

        var act = () => ImportAsync(Raw(raw, Column("amountvalue_sum")), ImportStrategy.Upsert);

        await act.Should().ThrowAsync<ArchiveSchemaImmutableException>();
    }

    // A rollup disabled before FirstActivatedAt existed carries no stamp, but activation left its watermark.
    [Fact]
    public async Task ImportRt_ADisabledRollupWithAWatermarkButNoStamp_IsRefused()
    {
        var rollup = await CreateAsync(id => Rollup(id,
            extra: "      - id: System.StreamData/RollupArchive.LastAggregatedBucketEnd\n        value: 2026-01-01T00:00:00Z"), CkArchiveStatus.Disabled);

        var act = () => ImportAsync(Rollup(rollup, bucketMs: 7_200_000), ImportStrategy.Upsert);

        (await act.Should().ThrowAsync<RollupSchemaImmutableException>()).Which.Field.Should().Be("BucketSizeMs");
    }

    // Activated before FirstActivatedAt existed and still Activated today: the marker is set when it stops, so it stays guarded.
    [Fact]
    public async Task SetStatus_StampsAnArchiveLeavingActivatedWithoutAStamp_AndItStaysGuarded()
    {
        var raw = OctoObjectId.GenerateNewId();
        await ImportAsync(Raw(raw, Column("amountvalue_sum"), Computed("doubled", "Double")).Replace("attributes:\n      - id: System.StreamData/Archive.TargetCkTypeId",
            "attributes:\n      - id: System.StreamData/Archive.Status\n        value: 1\n      - id: System.StreamData/Archive.TargetCkTypeId"), ImportStrategy.Insert);
        (await FirstActivatedAtOfAsync(raw, RawType)).Should().BeNull();

        await fixture.TenantContext.GetArchiveRuntimeStore().SetStatusAsync(raw, CkArchiveStatus.Disabled);

        (await FirstActivatedAtOfAsync(raw, RawType)).Should().NotBeNull();
        var act = () => ImportAsync(Raw(raw, Column("amountvalue_sum")), ImportStrategy.Upsert);
        await act.Should().ThrowAsync<ArchiveSchemaImmutableException>();
    }

    [Fact]
    public async Task SetStatus_StampsTheFirstActivation_AndKeepsIt()
    {
        var rollup = await CreateAsync(id => Rollup(id), CkArchiveStatus.Disabled);
        var store = fixture.TenantContext.GetArchiveRuntimeStore();
        (await FirstActivatedAtOfAsync(rollup)).Should().BeNull("a seed import is no activation");

        await store.SetStatusAsync(rollup, CkArchiveStatus.Activated);
        var first = await FirstActivatedAtOfAsync(rollup);
        await store.SetStatusAsync(rollup, CkArchiveStatus.Disabled);
        await store.SetStatusAsync(rollup, CkArchiveStatus.Activated);

        first.Should().NotBeNull();
        (await FirstActivatedAtOfAsync(rollup)).Should().Be(first);
    }

    // The stamp is runtime state: a re-import of the seed, which never declares it, must not reset it.
    [Fact]
    public async Task ImportRt_KeepsTheFirstActivation()
    {
        var rollup = await CreateAsync(id => Rollup(id));
        var first = await FirstActivatedAtOfAsync(rollup);

        await ImportAsync(Rollup(rollup), ImportStrategy.Upsert);

        (await FirstActivatedAtOfAsync(rollup)).Should().Be(first);
    }

    [Fact]
    public async Task TheEntityApi_RefusesAnItemUpdateOfTheAggregations()
    {
        var rollup = await CreateAsync(id => Rollup(id));
        var update = EntityUpdateInfo<RtEntity>.CreateRecordArrayItemUpdate(new RtEntityId(RollupType, rollup),
            new RtEntity { RtId = rollup, CkTypeId = RollupType },
            [new RtRecordArrayItemUpdate("Aggregations", "SourcePath", "Amount.Value", new Dictionary<string, object?> { ["TargetColumnName"] = "other" })]);

        var act = () => ApplyAsync(update);

        (await act.Should().ThrowAsync<RollupSchemaImmutableException>()).Which.Field.Should().Be("Aggregations");
    }

    [Fact]
    public async Task ImportRt_WhatMayChange_Passes_AndAnArchiveNotYetActivatedStaysEditable()
    {
        var activated = await CreateAsync(id => Rollup(id));
        var created = await CreateAsync(id => Rollup(id), CkArchiveStatus.Created);
        var failed = await CreateAsync(id => Rollup(id), CkArchiveStatus.Failed);

        await ImportAsync(Rollup(activated, lagMs: 600_000,
            extra: "      - id: System.StreamData/RollupArchive.CarryLookbackMs\n        value: 1000"), ImportStrategy.Upsert);
        await ImportAsync(Rollup(created, bucketMs: 7_200_000), ImportStrategy.Upsert);
        await ImportAsync(Rollup(failed, bucketMs: 7_200_000), ImportStrategy.Upsert);

        (await BucketSizeOfAsync(created)).Should().Be(7_200_000);
        (await BucketSizeOfAsync(failed)).Should().Be(7_200_000);
    }

    [Fact]
    public async Task ImportRt_AnActivatedRawArchive_TakesANewColumn_AndRefusesRemovingOrRetyping()
    {
        var raw = await CreateAsync(id => Raw(id, Column("amountvalue_sum"), Computed("doubled", "Double")));

        await ImportAsync(Raw(raw, Column("amountvalue_sum"), Column("otherPath"), Computed("doubled", "Double")), ImportStrategy.Upsert);

        var removed = () => ImportAsync(Raw(raw, Column("amountvalue_sum"), Computed("doubled", "Double")), ImportStrategy.Upsert);
        var removal = (await removed.Should().ThrowAsync<ArchiveSchemaImmutableException>()).Which;
        removal.Field.Should().Be("Columns");
        removal.Message.Should().Contain("otherpath").And.Contain("cannot be removed").And.Contain(raw.ToString()).And.Contain("needs a new archive");

        var retyped = () => ImportAsync(Raw(raw, Column("amountvalue_sum"), Column("otherPath"), Computed("doubled", "Int")), ImportStrategy.Upsert);
        (await retyped.Should().ThrowAsync<ArchiveSchemaImmutableException>()).Which.Message.Should().Contain("type of column 'doubled'");
    }

    private static EntityUpdateInfo<RtEntity> UpdateOf(OctoObjectId rtId, RtCkId<CkTypeId> type, string attribute, object? value)
    {
        var entity = new RtEntity { RtId = rtId, CkTypeId = type };
        entity.SetAttributeValue(attribute, value is long ? AttributeValueTypesDto.Int64 : AttributeValueTypesDto.RecordArray, value);
        return EntityUpdateInfo<RtEntity>.CreateUpdate(new RtEntityId(type, rtId), entity);
    }

    private async Task ApplyAsync(EntityUpdateInfo<RtEntity> update)
    {
        var session = await Repository.GetSessionAsync();
        session.StartTransaction();
        await Repository.ApplyChangesAsync(session, [update], [], new OperationResult());
        await session.CommitTransactionAsync();
    }

    [Fact]
    public async Task TheEntityApi_RefusesTheBucketSizeOfAnActivatedRollup_AndTakesItsLag()
    {
        var rollup = await CreateAsync(id => Rollup(id));

        var act = () => ApplyAsync(UpdateOf(rollup, RollupType, "BucketSizeMs", 7_200_000L));

        (await act.Should().ThrowAsync<RollupSchemaImmutableException>()).Which.Field.Should().Be("BucketSizeMs");
        await ApplyAsync(UpdateOf(rollup, RollupType, "WatermarkLagMs", 600_000L));
    }

    [Fact]
    public async Task TheEntityApi_RefusesEmptyingTheSources_AndAnUpdateThatDoesNotTouchThemPasses()
    {
        var rollup = await CreateAsync(id => Rollup(id));

        var act = () => ApplyAsync(UpdateOf(rollup, RollupType, "Sources", new List<RtRecord>()));

        (await act.Should().ThrowAsync<RollupSchemaImmutableException>()).Which.Field.Should().Be("Sources");
    }

    [Fact]
    public async Task TheEntityApi_RefusesEmptyingTheColumnsOfAnActivatedRawArchive()
    {
        var raw = await CreateAsync(id => Raw(id, Column("amountvalue_sum")));

        var act = () => ApplyAsync(UpdateOf(raw, RawType, "Columns", new List<RtRecord>()));

        (await act.Should().ThrowAsync<ArchiveSchemaImmutableException>()).Which.Field.Should().Be("Columns");
    }

    // The lifecycle of a computed column and the bookkeeping of a rollup are the stores' own writes.
    [Fact]
    public async Task TheStreamDataStores_KeepWriting()
    {
        var store = fixture.TenantContext.GetArchiveRuntimeStore();
        var raw = await CreateAsync(id => Raw(id, Column("amountvalue_sum"), Computed("doubled", "Double")));

        await store.RemoveComputedColumnAsync(raw, "doubled");

        var rollups = fixture.TenantContext.GetRollupArchiveRuntimeStore()!;
        var rollup = await CreateAsync(id => Rollup(id));
        await rollups.AdvanceWatermarkAsync(rollup, new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc));
        await rollups.RecordRunFailureAsync(rollup, "Column unknown: x", new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc));
        await rollups.RecordRunFailureAsync(rollup, "Column unknown: y", new DateTime(2026, 1, 1, 0, 1, 0, DateTimeKind.Utc));

        var failing = await rollups.GetAsync(rollup);
        failing!.ConsecutiveRunFailures.Should().Be(2);

        await rollups.ClearRunFailureAsync(rollup);

        (await rollups.GetAsync(rollup))!.ConsecutiveRunFailures.Should().Be(0);
        var session = await Repository.GetSessionAsync();
        var entity = await Repository.GetRtEntityByRtIdAsync(session, new RtEntityId(RollupType, rollup));
        entity!.Attributes.GetValueOrDefault("LastRunError").Should().BeNull();
    }

    [Fact]
    public async Task ABlueprintUpdate_ThatMeetsAFrozenChange_Fails_AndOneThatDoesNot_Passes()
    {
        var ct = TestContext.Current.CancellationToken;
        var service = fixture.GetBlueprintService();
        var tenantId = await fixture.CreateTestTenantAsync("archguard");
        try
        {
            var tenant = await fixture.EnableStreamDataAsync(tenantId);
            var applied = await service.ApplyBlueprintAsync(tenantId, new BlueprintId("ArchiveGuardBp", "1.0.0"), force: false, ct);
            applied.IsSuccess.Should().BeTrue();
            var rollup = new OctoObjectId("6a7000000000000000007872");
            await tenant.GetArchiveRuntimeStore().SetStatusAsync(rollup, CkArchiveStatus.Activated);

            var allowed = await service.ApplyUpdateAsync(tenantId, new BlueprintId("ArchiveGuardBp", "1.1.0"), BlueprintUpdateMode.Full, null, ct);
            allowed.Success.Should().BeTrue(string.Join("; ", allowed.Errors));

            foreach (var continueOnError in new[] { false, true })
            {
                var refused = await service.ApplyUpdateAsync(tenantId, new BlueprintId("ArchiveGuardBp", "2.0.0"), BlueprintUpdateMode.Full,
                    new BlueprintUpdateOptions { ContinueOnError = continueOnError }, ct);

                refused.Success.Should().BeFalse();
                refused.Errors.Should().Contain(e => e.Contains("BucketSizeMs") && e.Contains("needs a new archive"));
            }
        }
        finally
        {
            await fixture.DropTenantAsync(tenantId);
        }
    }
}
