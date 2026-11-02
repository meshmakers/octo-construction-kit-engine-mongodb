using System.Globalization;
using FluentAssertions;
using Meshmakers.Octo.ConstructionKit.Contracts;
using Meshmakers.Octo.ConstructionKit.Contracts.BlueprintCatalogs;
using Meshmakers.Octo.Runtime.Contracts.Blueprints;
using Meshmakers.Octo.Runtime.Contracts.MongoDb;
using Meshmakers.Octo.Runtime.Contracts.StreamData;
using Meshmakers.Octo.Runtime.Engine.CrateDb;
using Meshmakers.Octo.Runtime.Engine.MongoDb.IntegrationTests.Collections;
using Meshmakers.Octo.Runtime.Engine.MongoDb.IntegrationTests.Fixtures;
using Xunit;

namespace Meshmakers.Octo.Runtime.Engine.MongoDb.IntegrationTests;

/// <summary>
///     AB#7991 against MongoDB and a real CrateDB: a blueprint install activates the archives its seed creates (raw first,
///     rollups in source order, an excluded archive stays Created), queues the backfill of new rollups, never touches an
///     archive the tenant already holds (re-apply, forced re-apply, update), and a tenant without stream data keeps its
///     archives Created. The backfill of a rollup added by an update runs to completion through the recompute orchestrator,
///     and the rollup of that rollup converges to the same hourly means as the raw data.
/// </summary>
[Collection(BlueprintArchiveActivationCollection.Name)]
public class BlueprintArchiveActivationIntegrationTests(BlueprintArchiveActivationFixture fixture)
{
    private static readonly RtCkId<CkTypeId> MeasuringPointType = new("Test/MeasuringPoint");

    private static readonly OctoObjectId Raw = new("6a7991000000000000000a01");
    private static readonly OctoObjectId Excluded = new("6a7991000000000000000a02");
    private static readonly OctoObjectId Added = new("6a7991000000000000000a03");
    private static readonly OctoObjectId Rollup5m = new("6a7991000000000000000b01");
    private static readonly OctoObjectId Rollup1h = new("6a7991000000000000000b02");
    private static readonly OctoObjectId RollupTwa = new("6a7991000000000000000b03");

    private static readonly OctoObjectId BackfillRaw = new("6a7991000000000000000c01");
    private static readonly OctoObjectId Backfill5m = new("6a7991000000000000000d01");
    private static readonly OctoObjectId Backfill1h = new("6a7991000000000000000d02");

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static BlueprintId Activation(string version) => new("ArchiveActivationBp", version);

    private static async Task<CkArchiveStatus?> StatusAsync(ITenantContext tenant, OctoObjectId archive) =>
        (await tenant.GetArchiveRuntimeStore().GetAsync(archive))?.Status;

    private static BlueprintArchiveActivation Of(IEnumerable<BlueprintArchiveActivation> activations, OctoObjectId archive) =>
        activations.Single(a => a.ArchiveRtId == archive.ToString());

    [Fact]
    public async Task Install_ActivatesTheNewArchives_AndAReApplyTouchesNothing()
    {
        var service = fixture.GetBlueprintService();
        var tenantId = await fixture.CreateTestTenantAsync("bpact");
        try
        {
            var tenant = await fixture.EnableStreamDataAsync(tenantId);

            var installed = await service.ApplyBlueprintAsync(tenantId, Activation("1.0.0"), force: false, Ct);

            installed.IsSuccess.Should().BeTrue(string.Join("; ", installed.OperationResult.Messages));
            var activations = installed.ArchiveActivations;
            activations.Should().HaveCount(5);
            foreach (var archive in new[] { Raw, Rollup5m, Rollup1h, RollupTwa })
            {
                Of(activations, archive).Outcome.Should().Be(BlueprintArchiveActivationOutcome.Activated, archive.ToString());
                (await StatusAsync(tenant, archive)).Should().Be(CkArchiveStatus.Activated);
                (await TableExistsAsync(tenantId, archive)).Should().BeTrue();
            }

            Of(activations, Excluded).Outcome.Should().Be(BlueprintArchiveActivationOutcome.SkippedOptOut);
            (await StatusAsync(tenant, Excluded)).Should().Be(CkArchiveStatus.Created);

            // The sources are empty: nothing to backfill; the time-weighted rollup over raw is held back (AB#7984).
            Of(activations, Rollup5m).Backfill.Should().Be(BlueprintRollupBackfillOutcome.NothingToBackfill);
            Of(activations, Rollup1h).Backfill.Should().Be(BlueprintRollupBackfillOutcome.NothingToBackfill);
            Of(activations, RollupTwa).Backfill.Should().Be(BlueprintRollupBackfillOutcome.NotQueued);
            Of(activations, RollupTwa).BackfillReason.Should().Contain("AB#7984");
            installed.OperationResult.Messages.Should().Contain(m => m.MessageNumber == 71 && m.MessageText.Contains("AB#7984"));

            // A new rollup has a watermark (initialised by the activation), so the forward orchestrator picks it up.
            (await tenant.GetRollupArchiveRuntimeStore()!.GetAsync(Rollup5m))!.LastAggregatedBucketEnd.Should().NotBeNull();

            // An operator disables an archive on purpose; neither a re-apply nor a forced re-apply activates anything.
            await tenant.GetArchiveLifecycleService()!.DisableAsync(RollupTwa);
            foreach (var force in new[] { false, true })
            {
                var reapplied = await service.ApplyBlueprintAsync(tenantId, Activation("1.0.0"), force, Ct);

                reapplied.IsSuccess.Should().BeTrue();
                reapplied.ArchiveActivations.Should().OnlyContain(a =>
                    a.Outcome == BlueprintArchiveActivationOutcome.SkippedPreExisting);
                (await StatusAsync(tenant, RollupTwa)).Should().Be(CkArchiveStatus.Disabled);
                (await StatusAsync(tenant, Excluded)).Should().Be(CkArchiveStatus.Created);
            }

            // An update that adds an archive activates exactly that one.
            var updated = await service.ApplyUpdateAsync(tenantId, Activation("1.1.0"), BlueprintUpdateMode.Merge, null, Ct);

            updated.Success.Should().BeTrue(string.Join("; ", updated.Errors));
            Of(updated.ArchiveActivations, Added).Outcome.Should().Be(BlueprintArchiveActivationOutcome.Activated);
            updated.ArchiveActivations.Where(a => a.ArchiveRtId != Added.ToString())
                .Should().OnlyContain(a => a.Outcome == BlueprintArchiveActivationOutcome.SkippedPreExisting);
            (await StatusAsync(tenant, Added)).Should().Be(CkArchiveStatus.Activated);
            (await StatusAsync(tenant, RollupTwa)).Should().Be(CkArchiveStatus.Disabled);
            (await StatusAsync(tenant, Excluded)).Should().Be(CkArchiveStatus.Created);
        }
        finally
        {
            await ThrowawayTenant.DropAsync(fixture.GetSystemContext(), tenantId);
        }
    }

    [Fact]
    public async Task Install_WithoutStreamData_Succeeds_AndLeavesTheArchivesOut()
    {
        var service = fixture.GetBlueprintService();
        var tenantId = await fixture.CreateTestTenantAsync("bpoff");
        try
        {
            // The blueprint needs System.StreamData; a tenant that had stream data and switched it off still holds the model.
            var off = await fixture.EnableStreamDataAsync(tenantId);
            await off.DisableStreamDataAsync();

            var installed = await service.ApplyBlueprintAsync(tenantId, Activation("1.0.0"), force: false, Ct);

            installed.IsSuccess.Should().BeTrue(string.Join("; ", installed.OperationResult.Messages));
            installed.ArchiveActivations.Where(a => a.ArchiveRtId != Excluded.ToString())
                .Should().OnlyContain(a => a.Outcome == BlueprintArchiveActivationOutcome.SkippedStreamDataDisabled);
            installed.OperationResult.Messages.Should().Contain(m =>
                m.MessageNumber == 71 && m.MessageText.Contains("Stream data is not enabled"));
            var tenant = await fixture.GetSystemContext().FindTenantContextAsync(tenantId);
            // AB#7991: with stream data off the stream-data part of the seed is left out, so the archives are not created.
            foreach (var archive in new[] { Raw, Excluded, Rollup5m, Rollup1h, RollupTwa })
            {
                (await StatusAsync(tenant, archive)).Should().BeNull();
            }
        }
        finally
        {
            await ThrowawayTenant.DropAsync(fixture.GetSystemContext(), tenantId);
        }
    }

    [Fact]
    public async Task UpdateAddingRollups_BackfillsThem_AndTheRollupOfTheRollupConverges()
    {
        var service = fixture.GetBlueprintService();
        var tenantId = await fixture.CreateTestTenantAsync("bpfill");
        try
        {
            var tenant = await fixture.EnableStreamDataAsync(tenantId);
            (await service.ApplyBlueprintAsync(tenantId, new BlueprintId("ArchiveBackfillBp", "1.0.0"), force: false, Ct))
                .IsSuccess.Should().BeTrue();
            (await StatusAsync(tenant, BackfillRaw)).Should().Be(CkArchiveStatus.Activated);

            // Three hours of one value a minute, two days back: hour h holds h*100 + 0..59, mean h*100 + 29.5.
            var start = DateTime.UtcNow.Date.AddDays(-2).AddHours(6);
            var meter = OctoObjectId.GenerateNewId();
            var points = Enumerable.Range(0, 180).Select(i => new StreamDataPoint
            {
                RtId = meter,
                CkTypeId = MeasuringPointType,
                Timestamp = start.AddMinutes(i),
                Attributes = new Dictionary<string, object?> { ["CounterReading"] = (double)(i / 60 * 100 + i % 60) }
            }).ToList();
            (await tenant.GetStreamDataRepository()!.InsertAsync(BackfillRaw, points)).Stored.Should().Be(180);
            await fixture.ScalarAsync($"REFRESH TABLE {TenantSchema.QualifiedArchiveTable(tenantId, BackfillRaw.ToString())}");

            var updated = await service.ApplyUpdateAsync(tenantId, new BlueprintId("ArchiveBackfillBp", "1.1.0"),
                BlueprintUpdateMode.Merge, null, Ct);

            updated.Success.Should().BeTrue(string.Join("; ", updated.Errors));
            Of(updated.ArchiveActivations, BackfillRaw).Outcome.Should().Be(BlueprintArchiveActivationOutcome.SkippedPreExisting);
            var fiveMinutes = Of(updated.ArchiveActivations, Backfill5m);
            fiveMinutes.Outcome.Should().Be(BlueprintArchiveActivationOutcome.Activated);
            fiveMinutes.Backfill.Should().Be(BlueprintRollupBackfillOutcome.Queued);
            fiveMinutes.BackfillJobRtId.Should().NotBeNull();
            var hourly = Of(updated.ArchiveActivations, Backfill1h);
            hourly.Outcome.Should().Be(BlueprintArchiveActivationOutcome.Activated);
            // Its source (the 5-minute rollup) is still empty: it fills when that backfill propagates.
            hourly.Backfill.Should().Be(BlueprintRollupBackfillOutcome.NothingToBackfill);

            // The backfill is a durable background job: drive it like the host's recompute tick does.
            var orchestrator = tenant.GetRecomputeOrchestrator()!;
            var jobStore = tenant.GetRecomputeJobStore();
            var jobId = new OctoObjectId(fiveMinutes.BackfillJobRtId!);
            for (var tick = 0; tick < 10; tick++)
            {
                await orchestrator.TickAsync(Ct);
                if ((await jobStore.GetAsync(jobId))?.State == RecomputeJobState.Completed &&
                    await RowCountAsync(tenantId, Backfill1h) == 3)
                {
                    break;
                }
            }

            (await jobStore.GetAsync(jobId))!.State.Should().Be(RecomputeJobState.Completed);
            (await RowCountAsync(tenantId, Backfill5m)).Should().Be(36);
            var means = await HourlyMeansAsync(tenantId, Backfill1h, start);
            means.Should().Equal(29.5, 129.5, 229.5);
        }
        finally
        {
            await ThrowawayTenant.DropAsync(fixture.GetSystemContext(), tenantId);
        }
    }

    private async Task<bool> TableExistsAsync(string tenantId, OctoObjectId archive)
    {
        var table = TenantSchema.QualifiedArchiveTable(tenantId, archive.ToString()).Replace("\"", string.Empty).Split('.');
        var count = await fixture.ScalarAsync(
            $"SELECT count(*) FROM information_schema.tables WHERE table_schema = '{table[0]}' AND table_name = '{table[1]}'");
        return Convert.ToInt64(count, CultureInfo.InvariantCulture) == 1;
    }

    private async Task<long> RowCountAsync(string tenantId, OctoObjectId rollup)
    {
        var table = TenantSchema.QualifiedArchiveTable(tenantId, rollup.ToString());
        await fixture.ScalarAsync($"REFRESH TABLE {table}");
        return Convert.ToInt64(await fixture.ScalarAsync($"SELECT count(*) FROM {table}"), CultureInfo.InvariantCulture);
    }

    private async Task<double[]> HourlyMeansAsync(string tenantId, OctoObjectId rollup, DateTime start)
    {
        var table = TenantSchema.QualifiedArchiveTable(tenantId, rollup.ToString());
        var means = new List<double>();
        await fixture.ScalarAsync($"REFRESH TABLE {table}");
        for (var hour = 0; hour < 3; hour++)
        {
            var window = start.AddHours(hour).ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture);
            var mean = await fixture.ScalarAsync(
                $"SELECT sum(counterreading_avg_sum) / sum(counterreading_avg_count) FROM {table} WHERE window_start = '{window}'");
            means.Add(Math.Round(Convert.ToDouble(mean, CultureInfo.InvariantCulture), 6));
        }

        return means.ToArray();
    }
}
