using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using Meshmakers.Octo.ConstructionKit.Contracts;
using Meshmakers.Octo.Runtime.Contracts.StreamData;
using Meshmakers.Octo.Runtime.Engine.CrateDb;
using Meshmakers.Octo.Runtime.Engine.CrateDb.Copy;
using Xunit;

namespace Meshmakers.Octo.Runtime.Engine.MongoDb.IntegrationTests.StagingCopy;

/// <summary>
///     AB#5967 (slice AB#6967) against a real CrateDB: the native copy of an archive table to and from a staging
///     location (<c>file://</c> in the container), the credential protection around it, and the guard on
///     <c>stats.jobs_log_filter</c>.
/// </summary>
[Collection(CrateStagingCopyCollection.Name)]
public class StagingCopyIntegrationTests(CrateStagingCopyFixture fixture)
{
    // A credential that must not show up anywhere. The secret contains '/' and '+' as a real S3 secret does.
    private const string CanaryKeyId = "CANARYKEYID1234";
    private const string CanarySecret = "CANARY/SECRET+value==";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static ArchiveCopyStagingLocation UnreachableS3(string host = "127.0.0.1:1") => new(
        $"s3://{CanaryKeyId}:{CanarySecret}@{host}/octo-artifacts/copy-staging/t1/run1",
        new Dictionary<string, string> { ["protocol"] = "http" });

    private static async Task<List<ArchiveCopySliceResult>> Collect(IAsyncEnumerable<ArchiveCopySliceResult> results)
    {
        var list = new List<ArchiveCopySliceResult>();
        await foreach (var result in results)
        {
            list.Add(result);
        }

        return list;
    }

    /// <summary>Row count, sum and time bounds per month: what a restored table must have in common with its source.</summary>
    private async Task<List<string>> FingerprintAsync(string tenantId, OctoObjectId archiveRtId)
    {
        var table = TenantSchema.QualifiedArchiveTable(tenantId, archiveRtId.ToString());
        var rows = await fixture.QueryAsync(
            "SELECT date_trunc('month', \"timestamp\") AS m, COUNT(*) AS c, SUM(\"value\") AS s, " +
            $"MIN(\"timestamp\") AS lo, MAX(\"timestamp\") AS hi FROM {table} GROUP BY 1 ORDER BY 1");
        return rows.Select(r => string.Join("|", r.Values.Select(v => Convert.ToString(v, CultureInfo.InvariantCulture)))).ToList();
    }

    private async Task<(string Replicas, long? Refresh)> TableSettingsAsync(string tenantId, OctoObjectId archiveRtId)
    {
        var row = (await fixture.QueryAsync(
            "SELECT number_of_replicas, settings['refresh_interval'] AS refresh FROM information_schema.tables " +
            $"WHERE table_schema = '{TenantSchema.SchemaName(tenantId)}' AND table_name = '{TenantSchema.ArchiveTableName(archiveRtId.ToString())}'"))
            .Single();
        return (Convert.ToString(row["number_of_replicas"], CultureInfo.InvariantCulture)!,
            row["refresh"] is null ? null : Convert.ToInt64(row["refresh"], CultureInfo.InvariantCulture));
    }

    // ---- COPY TO and COPY FROM -----------------------------------------------------------------------------

    [Fact]
    public async Task DumpAndLoad_RoundTrip_GivesTheSameRowsSumsAndTimeBoundsPerMonth_UnderAnotherTenantId()
    {
        var tenantA = CrateStagingCopyFixture.NewTenantId();
        var tenantB = CrateStagingCopyFixture.NewTenantId();
        var source = OctoObjectId.GenerateNewId();
        var target = OctoObjectId.GenerateNewId();
        await fixture.CreateRawArchiveAsync(tenantA, source, hourlyRows: 1500); // 62 days: January to March
        await fixture.CreateRawArchiveAsync(tenantB, target);
        var repoA = fixture.NewRepository(tenantA, source);
        var repoB = fixture.NewRepository(tenantB, target);
        var location = CrateStagingCopyFixture.NewFileLocation();
        var options = new ArchiveCopyOptions { TargetSliceRows = 400 };

        var plan = await repoA.PlanArchiveCopyAsync(source, options, Ct);
        await fixture.PrepareDirectoriesAsync(plan, location);
        var dumped = await Collect(repoA.CopyToStagingAsync(source, plan, location, options, Ct));
        var loaded = await Collect(repoB.CopyFromStagingAsync(target, plan, location, options, Ct));

        Assert.True(repoA.SupportsStagingCopy);
        Assert.True(plan.Slices.Count >= 3, $"{plan.Slices.Count} slices");
        Assert.Equal(1500, plan.TotalRows);
        Assert.All(plan.Slices, s => Assert.InRange(s.RowCount, 1, options.TargetSliceRows * 2));

        // dump: one folder with files per planned slice, rows as counted
        Assert.All(dumped, r => Assert.True(r.IsSuccess, r.Message));
        Assert.Equal(plan.Slices.Select(s => s.RowCount), dumped.Select(r => r.CopiedRows));
        foreach (var slice in plan.Slices)
        {
            var folder = CrateStagingCopyFixture.Path(location.WithSubPath(slice.GetStagingPath(source)));
            var (exit, listing) = await fixture.ExecAsync("sh", "-c", $"ls {folder}");
            Assert.True(exit == 0 && listing.Contains(".json.gz", StringComparison.Ordinal), $"{folder}: {listing}");
        }

        // load: every slice reports the manifest count and no error rows
        Assert.All(loaded, r => Assert.True(r.IsSuccess, r.Message));
        Assert.Equal(plan.Slices.Select(s => s.RowCount), loaded.Select(r => r.CopiedRows));
        Assert.All(loaded, r => Assert.Equal(0, r.ErrorRows));

        var expected = await FingerprintAsync(tenantA, source);
        Assert.True(expected.Count >= 3);
        Assert.Equal(expected, await FingerprintAsync(tenantB, target));
    }

    [Fact]
    public async Task Readiness_ReportsTheFilterAndTheVersion_WithoutChangingAnything()
    {
        var repo = fixture.NewRepository(CrateStagingCopyFixture.NewTenantId());

        var ready = await repo.CheckStagingCopyReadinessAsync(Ct);

        Assert.True(ready.IsReady);
        Assert.Equal("5.10.10", ready.StorageVersion);
        Assert.Null(ready.Reason);
        try
        {
            await fixture.SetJobLogFilterAsync(null);
            var notReady = await repo.CheckStagingCopyReadinessAsync(Ct);
            Assert.False(notReady.IsReady);
            Assert.True(notReady.Supported);
            Assert.False(notReady.JobLogFilterSet);
            Assert.Contains("stats.jobs_log_filter", notReady.Reason, StringComparison.Ordinal);
            Assert.DoesNotContain("CANARY", notReady.Reason, StringComparison.Ordinal);
        }
        finally
        {
            await fixture.SetJobLogFilterAsync(JobLogFilterGuard.RecommendedFilter);
        }
    }

    [Fact]
    public async Task Plan_OnARealTable_PartitionsTheRows_AndAnEmptyTableHasNoSlices()
    {
        var tenant = CrateStagingCopyFixture.NewTenantId();
        var full = OctoObjectId.GenerateNewId();
        var empty = OctoObjectId.GenerateNewId();
        await fixture.CreateRawArchiveAsync(tenant, full, hourlyRows: 1000);
        await fixture.CreateRawArchiveAsync(tenant, empty);
        var repo = fixture.NewRepository(tenant, full, empty);

        Assert.Equal(1000, await fixture.CountAsync($"SELECT COUNT(*) FROM {TenantSchema.QualifiedArchiveTable(tenant, full.ToString())}"));
        var plan = await repo.PlanArchiveCopyAsync(full, new ArchiveCopyOptions { TargetSliceRows = 300 }, Ct);
        var emptyPlan = await repo.PlanArchiveCopyAsync(empty, null, Ct);

        Assert.Equal(1000, plan.TotalRows);
        for (var i = 1; i < plan.Slices.Count; i++)
        {
            Assert.Equal(plan.Slices[i - 1].ToUtc, plan.Slices[i].FromUtc); // no gap, no overlap
        }

        Assert.Empty(emptyPlan.Slices);
    }

    [Fact]
    public async Task DescribeAndVersion_ReturnEveryColumnWithItsTypeAndTheClusterVersion()
    {
        var tenant = CrateStagingCopyFixture.NewTenantId();
        var archive = OctoObjectId.GenerateNewId();
        await fixture.CreateRawArchiveAsync(tenant, archive);
        var repo = fixture.NewRepository(tenant, archive);

        var table = await repo.DescribeArchiveTableAsync(archive, ArchiveCopyTable.Data, Ct);
        var version = await repo.GetStorageVersionAsync(Ct);

        var columns = table.Columns.ToDictionary(c => c.Name, c => c.DataType);
        Assert.Equal("double precision", columns["value"]);
        Assert.Equal("timestamp with time zone", columns["timestamp"]);
        Assert.Contains("rtid", columns.Keys);
        Assert.Contains("cktypeid", columns.Keys);
        Assert.Equal("5.10.10", version);
        Assert.Empty((await repo.DescribeArchiveTableAsync(archive, ArchiveCopyTable.GenerationMap, Ct)).Columns);
    }

    [Fact]
    public async Task ALoadThatFailsInTheMiddle_PutsReplicasAndRefreshBack_AndARerunWithOverwriteDuplicatesLeavesNoDuplicates()
    {
        var tenant = CrateStagingCopyFixture.NewTenantId();
        var source = OctoObjectId.GenerateNewId();
        var target = OctoObjectId.GenerateNewId();
        await fixture.CreateRawArchiveAsync(tenant, source, hourlyRows: 1500);
        await fixture.CreateRawArchiveAsync(tenant, target);
        var repo = fixture.NewRepository(tenant, source, target);
        var location = CrateStagingCopyFixture.NewFileLocation();
        var options = new ArchiveCopyOptions { TargetSliceRows = 400 };
        var plan = await repo.PlanArchiveCopyAsync(source, options, Ct);
        await fixture.PrepareDirectoriesAsync(plan, location);
        await Collect(repo.CopyToStagingAsync(source, plan, location, options, Ct));
        var before = await TableSettingsAsync(tenant, target);

        // the files of the second slice disappear (an object store that lost them)
        var lost = CrateStagingCopyFixture.Path(location.WithSubPath(plan.Slices[1].GetStagingPath(source)));
        Assert.Equal(0, (await fixture.ExecAsync("rm", "-rf", lost)).ExitCode);

        var failed = await Collect(repo.CopyFromStagingAsync(target, plan, location, options, Ct));

        Assert.Equal([ArchiveCopySliceStatus.Succeeded, ArchiveCopySliceStatus.Failed], failed.Select(r => r.Status));
        var after = await TableSettingsAsync(tenant, target);
        Assert.Equal(before.Replicas, after.Replicas);
        Assert.Equal(before.Refresh ?? 1000, after.Refresh);

        // dump again, then load every slice a second time; the first one is a repeat
        await fixture.PrepareDirectoriesAsync(plan, location);
        var redump = await Collect(repo.CopyToStagingAsync(source, plan, location, options, Ct));
        Assert.All(redump, r => Assert.True(r.IsSuccess, r.Message));
        var rerun = await Collect(repo.CopyFromStagingAsync(target, plan, location, options with { OverwriteDuplicates = true }, Ct));

        Assert.All(rerun, r => Assert.True(r.IsSuccess, r.Message));
        Assert.Equal(1500, await fixture.CountAsync($"SELECT COUNT(*) FROM {TenantSchema.QualifiedArchiveTable(tenant, target.ToString())}"));
        Assert.Equal(await FingerprintAsync(tenant, source), await FingerprintAsync(tenant, target));
    }

    [Fact]
    public async Task ALoadIntoATableWithoutADumpedColumn_StopsBeforeLoading_NamingTheColumn()
    {
        var tenant = CrateStagingCopyFixture.NewTenantId();
        var source = OctoObjectId.GenerateNewId();
        var target = OctoObjectId.GenerateNewId();
        await fixture.CreateRawArchiveAsync(tenant, source, hourlyRows: 100);
        await fixture.CreateRawArchiveAsync(tenant, target, withValueColumn: false);
        var repo = fixture.NewRepository(tenant, source, target);
        var location = CrateStagingCopyFixture.NewFileLocation();
        var plan = await repo.PlanArchiveCopyAsync(source, null, Ct);
        await fixture.PrepareDirectoriesAsync(plan, location);
        await Collect(repo.CopyToStagingAsync(source, plan, location, null, Ct));

        var ex = await Assert.ThrowsAsync<ArchiveCopyRefusedException>(() =>
            Collect(repo.CopyFromStagingAsync(target, plan, location, null, Ct)));

        Assert.Equal(ArchiveCopyRefusalReason.ColumnMissing, ex.Reason);
        Assert.Contains("'value'", ex.Message);
        Assert.Equal(0, await fixture.CountAsync($"SELECT COUNT(*) FROM {TenantSchema.QualifiedArchiveTable(tenant, target.ToString())}"));
    }

    [Fact]
    public async Task ALoadThatNeedsMoreDiskThanTheNodeHas_IsRefusedWithBothSizes_BeforeAnythingChanges()
    {
        var tenant = CrateStagingCopyFixture.NewTenantId();
        var source = OctoObjectId.GenerateNewId();
        var target = OctoObjectId.GenerateNewId();
        await fixture.CreateRawArchiveAsync(tenant, source, hourlyRows: 100);
        await fixture.CreateRawArchiveAsync(tenant, target);
        var repo = fixture.NewRepository(tenant, source, target);
        var plan = await repo.PlanArchiveCopyAsync(source, null, Ct);
        var huge = plan with { Slices = plan.Slices.Select(s => s.WithStagedBytes(1L << 50)).ToList() }; // 1 PiB of staged files
        var before = await TableSettingsAsync(tenant, target);

        var ex = await Assert.ThrowsAsync<ArchiveCopyRefusedException>(() =>
            Collect(repo.CopyFromStagingAsync(target, huge, CrateStagingCopyFixture.NewFileLocation(), null, Ct)));

        Assert.Equal(ArchiveCopyRefusalReason.InsufficientDiskSpace, ex.Reason);
        Assert.Contains("per node", ex.Message);
        Assert.Contains("available", ex.Message);
        Assert.Equal(before, await TableSettingsAsync(tenant, target));
    }

    // ---- credential protection -----------------------------------------------------------------------------

    [Fact]
    public async Task TheGuard_RefusesWhileNoFilterKeepsCopyOutOfTheJobLog_AndPassesWhenItIsSet()
    {
        var tenant = CrateStagingCopyFixture.NewTenantId();
        var source = OctoObjectId.GenerateNewId();
        await fixture.CreateRawArchiveAsync(tenant, source, hourlyRows: 50);
        var repo = fixture.NewRepository(tenant, source);
        var plan = await repo.PlanArchiveCopyAsync(source, null, Ct);

        try
        {
            await fixture.SetJobLogFilterAsync(null);
            var ex = await Assert.ThrowsAsync<ArchiveCopyRefusedException>(() =>
                Collect(repo.CopyToStagingAsync(source, plan, CrateStagingCopyFixture.NewFileLocation(), null, Ct)));
            Assert.Equal(ArchiveCopyRefusalReason.JobLogNotFiltered, ex.Reason);
            Assert.Contains("stats.jobs_log_filter", ex.Message);

            await fixture.SetJobLogFilterAsync(JobLogFilterGuard.RecommendedFilter);
            var passing = CrateStagingCopyFixture.NewFileLocation();
            await fixture.PrepareDirectoriesAsync(plan, passing);
            var results = await Collect(repo.CopyToStagingAsync(source, plan, passing, null, Ct));
            Assert.All(results, r => Assert.True(r.IsSuccess, r.Message));
        }
        finally
        {
            await fixture.SetJobLogFilterAsync(JobLogFilterGuard.RecommendedFilter);
        }
    }

    [Fact]
    public async Task WithTheFilterSet_ACopyLeavesNoEntryInTheJobLog_WhileOtherStatementsAreRecorded_AndWithoutItTheCredentialIsReadable()
    {
        var tenant = CrateStagingCopyFixture.NewTenantId();
        var source = OctoObjectId.GenerateNewId();
        await fixture.CreateRawArchiveAsync(tenant, source, hourlyRows: 50);
        var table = TenantSchema.QualifiedArchiveTable(tenant, source.ToString());
        var timeout = TimeSpan.FromSeconds(10);

        // every statement carries a marker of its own, so the job log can be asked about exactly this run
        CopyStatement Copy(string marker) => CopyStatement.CopyTo(table, "timestamp", DateTime.UnixEpoch,
            DateTime.UtcNow.AddYears(10), new ArchiveCopyStagingLocation(
                $"s3://{CanaryKeyId}:{CanarySecret}@127.0.0.1:1/octo-artifacts/{marker}",
                new Dictionary<string, string> { ["protocol"] = "http" }));

        var unfiltered = "unfiltered" + Guid.NewGuid().ToString("N");
        var filtered = "filtered" + Guid.NewGuid().ToString("N");
        var selected = "selected" + Guid.NewGuid().ToString("N");

        try
        {
            // The problem the filter solves: without it, key and secret are readable in sys.jobs_log.
            await fixture.SetJobLogFilterAsync(null);
            await Task.Delay(500, Ct);
            await Assert.ThrowsAnyAsync<Exception>(() => fixture.Client.ExecuteStatementAsync(tenant, Copy(unfiltered), timeout, Ct));
            await Task.Delay(1000, Ct);
            Assert.True(await fixture.CountAsync($"SELECT COUNT(*) FROM sys.jobs_log WHERE stmt LIKE '%{unfiltered}%' AND stmt LIKE '%{CanaryKeyId}%'") > 0,
                "the control did not show the credential in sys.jobs_log; the test setup is wrong");

            await fixture.SetJobLogFilterAsync(JobLogFilterGuard.RecommendedFilter);
            await Task.Delay(500, Ct);
            await Assert.ThrowsAnyAsync<Exception>(() => fixture.Client.ExecuteStatementAsync(tenant, Copy(filtered), timeout, Ct));
            await fixture.Client.ExecuteStatementAsync(tenant, CopyStatement.Plain($"SELECT '{selected}'", true), timeout, Ct);
            await Task.Delay(1000, Ct);

            Assert.Equal(0, await fixture.CountAsync($"SELECT COUNT(*) FROM sys.jobs_log WHERE stmt LIKE '%{filtered}%'"));
            Assert.True(await fixture.CountAsync($"SELECT COUNT(*) FROM sys.jobs_log WHERE stmt LIKE '%{selected}%' AND stmt NOT LIKE '%sys.jobs_log%'") > 0,
                "other statements of the session are no longer recorded");
        }
        finally
        {
            await fixture.SetJobLogFilterAsync(JobLogFilterGuard.RecommendedFilter);
        }
    }

    [Fact]
    public async Task AFailingCopy_ThrowsAnExceptionWithoutTheCredential_NoInnerExceptionAndNoData()
    {
        var tenant = CrateStagingCopyFixture.NewTenantId();
        var source = OctoObjectId.GenerateNewId();
        await fixture.CreateRawArchiveAsync(tenant, source, hourlyRows: 50);
        var table = TenantSchema.QualifiedArchiveTable(tenant, source.ToString());
        var statement = CopyStatement.CopyTo(table, "timestamp", DateTime.UnixEpoch, DateTime.UtcNow.AddYears(10), UnreachableS3());

        var ex = await Assert.ThrowsAnyAsync<Exception>(() =>
            fixture.Client.ExecuteStatementAsync(tenant, statement, TimeSpan.FromSeconds(10), Ct));

        Assert.True(ex is StreamDataCopyStatementException or StreamDataStatementTimeoutException, ex.GetType().FullName);
        AssertNoCredential(ex);
        Assert.Null(ex.InnerException);
        Assert.Empty(ex.Data);
        Assert.Contains("s3://***@127.0.0.1:1/octo-artifacts/copy-staging/t1/run1", ex.Message);
    }

    [Fact]
    public async Task ACopyThatRunsOutOfItsOwnTimeout_IsAMaintenanceTimeoutWithTheRedactedStatement()
    {
        var tenant = CrateStagingCopyFixture.NewTenantId();
        var source = OctoObjectId.GenerateNewId();
        await fixture.CreateRawArchiveAsync(tenant, source, hourlyRows: 50);
        var table = TenantSchema.QualifiedArchiveTable(tenant, source.ToString());
        // 192.0.2.1 (TEST-NET-1) is never routed: CrateDB's connection attempt hangs until the timeout ends the call.
        var statement = CopyStatement.CopyTo(table, "timestamp", DateTime.UnixEpoch, DateTime.UtcNow.AddYears(10), UnreachableS3("192.0.2.1:9"));
        var stopwatch = Stopwatch.StartNew();

        var ex = await Assert.ThrowsAnyAsync<Exception>(() =>
            fixture.Client.ExecuteStatementAsync(tenant, statement, TimeSpan.FromSeconds(2), Ct));

        AssertNoCredential(ex);
        Assert.Null(ex.InnerException);
        Assert.True(stopwatch.Elapsed < TimeSpan.FromSeconds(30), "the call was not ended by its timeout");
        if (ex is StreamDataStatementTimeoutException timeout)
        {
            Assert.Equal(CrateStatementClass.Maintenance, timeout.StatementClass);
            Assert.Equal(TimeSpan.FromSeconds(2), timeout.Budget);
        }
        else
        {
            Assert.IsType<StreamDataCopyStatementException>(ex); // the network refused it faster than the timeout
        }
    }

    [Fact]
    public async Task ASliceOfAFailingStore_IsReportedAsFailedWithARedactedMessage_AndEarlierSlicesStayValid()
    {
        var tenant = CrateStagingCopyFixture.NewTenantId();
        var source = OctoObjectId.GenerateNewId();
        await fixture.CreateRawArchiveAsync(tenant, source, hourlyRows: 600);
        var repo = fixture.NewRepository(tenant, source);
        var options = new ArchiveCopyOptions { TargetSliceRows = 200, CommandTimeout = TimeSpan.FromSeconds(10) };
        var plan = await repo.PlanArchiveCopyAsync(source, options, Ct);

        var results = await Collect(repo.CopyToStagingAsync(source, plan, UnreachableS3(), options, Ct));

        var failed = Assert.Single(results);
        Assert.False(failed.IsSuccess);
        Assert.Equal(plan.Slices[0], failed.Slice);
        Assert.False(string.IsNullOrWhiteSpace(failed.Message));
        AssertNoCredential(failed.Message!);
    }

    [Fact]
    public async Task ANonCopyStatementThatFails_KeepsItsCurrentFailure()
    {
        var tenant = CrateStagingCopyFixture.NewTenantId();

        var ex = await Assert.ThrowsAnyAsync<Exception>(() =>
            fixture.Client.ExecuteNonQueryAsync(tenant, $"DELETE FROM \"{TenantSchema.SchemaName(tenant)}\".\"no_such_table\" WHERE true", Ct));

        Assert.IsAssignableFrom<Npgsql.PostgresException>(ex);
        Assert.Contains("unknown", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task TheDriversTracing_RecordsOrdinaryCommands_ButNotACopy()
    {
        var tenant = CrateStagingCopyFixture.NewTenantId();
        var source = OctoObjectId.GenerateNewId();
        await fixture.CreateRawArchiveAsync(tenant, source, hourlyRows: 20);
        var table = TenantSchema.QualifiedArchiveTable(tenant, source.ToString());
        var spans = new ConcurrentBag<string>();
        using var listener = new ActivityListener
        {
            ShouldListenTo = s => s.Name == "Npgsql",
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllDataAndRecorded,
            ActivityStopped = a => spans.Add(a.OperationName + " " + string.Join(" ", a.TagObjects.Select(t => $"{t.Key}={t.Value}")))
        };
        ActivitySource.AddActivityListener(listener);

        await fixture.Client.ExecuteStatementAsync(tenant, CopyStatement.Plain("SELECT 'traced-marker-5967'", true), TimeSpan.FromSeconds(10), Ct);
        await Assert.ThrowsAnyAsync<Exception>(() => fixture.Client.ExecuteStatementAsync(tenant,
            CopyStatement.CopyTo(table, "timestamp", DateTime.UnixEpoch, DateTime.UtcNow.AddYears(10), UnreachableS3()),
            TimeSpan.FromSeconds(10), Ct));
        var local = CrateStagingCopyFixture.NewFileLocation();
        await fixture.PrepareDirectoryAsync(local);
        await fixture.Client.ExecuteStatementAsync(tenant,
            CopyStatement.CopyTo(table, "timestamp", DateTime.UnixEpoch, DateTime.UtcNow.AddYears(10), local),
            TimeSpan.FromSeconds(30), Ct);
        listener.Dispose();

        Assert.Contains(spans, s => s.Contains("traced-marker-5967", StringComparison.Ordinal));
        Assert.DoesNotContain(spans, s => s.Contains("COPY", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(spans, s => s.Contains("CANARY", StringComparison.Ordinal));
    }

    private static void AssertNoCredential(Exception ex)
    {
        for (var current = ex; current is not null; current = current.InnerException!)
        {
            AssertNoCredential(current.Message);
            AssertNoCredential(current.ToString());
            AssertNoCredential(current.StackTrace ?? string.Empty);
            foreach (var value in current.Data.Values)
            {
                AssertNoCredential(Convert.ToString(value, CultureInfo.InvariantCulture) ?? string.Empty);
            }

            if (current.InnerException is null)
            {
                break;
            }
        }
    }

    private static void AssertNoCredential(string text)
    {
        Assert.DoesNotContain(CanaryKeyId, text, StringComparison.Ordinal);
        Assert.DoesNotContain(CanarySecret, text, StringComparison.Ordinal);
        Assert.DoesNotContain("CANARY/SECRET", text, StringComparison.Ordinal);
        Assert.DoesNotContain("SECRET+value", text, StringComparison.Ordinal);
    }
}
