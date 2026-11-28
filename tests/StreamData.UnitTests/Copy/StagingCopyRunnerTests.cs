using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Meshmakers.Octo.ConstructionKit.Contracts;
using Meshmakers.Octo.Runtime.Contracts.StreamData;
using Meshmakers.Octo.Runtime.Engine.CrateDb;
using Meshmakers.Octo.Runtime.Engine.CrateDb.Copy;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Meshmakers.Octo.Runtime.Engine.UnitTests.Copy;

/// <summary>
/// AB#5967 (slice AB#6967): planning, COPY TO and COPY FROM of one archive table against a simulated CrateDB
/// that parses the statements the runner sends.
/// </summary>
public class StagingCopyRunnerTests
{
    private const string CanarySecret = "CANARY/SECRET+value==";
    private static readonly OctoObjectId Source = OctoObjectId.GenerateNewId();
    private static readonly OctoObjectId Restored = OctoObjectId.GenerateNewId();

    private static readonly ArchiveCopyTarget SourceTarget =
        new(Source, "tenant1", SimulatedCrate.SourceTable, "timestamp");

    private static readonly ArchiveCopyTarget RestoreTarget =
        new(Restored, "tenant1", SimulatedCrate.TargetTable, "timestamp");

    private static readonly ArchiveCopyStagingLocation Location = new(
        $"s3://CANARYKEYID1234:{CanarySecret}@minio.octo-infra:9000/octo-artifacts/copy-staging/t1/run1",
        new Dictionary<string, string> { ["protocol"] = "http", ["sas_token"] = "CANARYSAS" });

    private static readonly ArchiveCopyOptions Small = new() { TargetSliceRows = 4 };
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private readonly SimulatedCrate _crate = new();

    private StagingCopyRunner Runner() => new(NullLogger.Instance, "tenant1", _crate);

    private static DateTime At(int day, int hour) => new(2026, 3, day, hour, 0, 0, DateTimeKind.Utc);

    /// <summary>A dense first week, a gap of three weeks, and a dense last two days: 17 rows.</summary>
    private SimulatedCrate WithData()
    {
        for (var h = 0; h < 10; h++) _crate.SourceRows.Add(At(1, h));
        for (var h = 0; h < 4; h++) _crate.SourceRows.Add(At(2, h * 6));
        for (var h = 0; h < 3; h++) _crate.SourceRows.Add(At(28, h));
        return _crate;
    }

    private static async Task<List<ArchiveCopySliceResult>> Collect(IAsyncEnumerable<ArchiveCopySliceResult> results)
    {
        var list = new List<ArchiveCopySliceResult>();
        await foreach (var r in results)
        {
            list.Add(r);
        }

        return list;
    }

    // ---- planning ------------------------------------------------------------------------------------------

    [Fact]
    public async Task Plan_PartitionsTheRowsIntoCountedAscendingSlices_OfAboutTheTarget()
    {
        WithData();

        var plan = await Runner().PlanAsync(SourceTarget, Small, Ct);

        Assert.Equal(Source, plan.ArchiveRtId);
        Assert.Equal(17, plan.TotalRows);
        Assert.All(plan.Slices, s => Assert.InRange(s.RowCount, 1, Small.TargetSliceRows * 2));
        Assert.All(plan.Slices, s => Assert.Equal(ArchiveCopyTable.Data, s.Table));
        // ascending, no gap and no overlap
        for (var i = 1; i < plan.Slices.Count; i++)
        {
            Assert.True(plan.Slices[i - 1].ToUtc <= plan.Slices[i].FromUtc);
        }

        // every slice counts exactly the rows of its range
        Assert.All(plan.Slices, s => Assert.Equal(s.RowCount,
            _crate.SourceRows.Count(r => r >= s.FromUtc && r < s.ToUtc)));
        Assert.Equal(["timestamp", "rtid", "value"], plan.Tables.Single().Columns.Select(c => c.Name));
    }

    [Fact]
    public async Task Plan_OfATableWithoutRowsOrWithoutTable_HasNoSlices()
    {
        var empty = await Runner().PlanAsync(SourceTarget, Small, Ct);
        Assert.Empty(empty.Slices);

        _crate.SourceColumns = [];
        var missing = await Runner().PlanAsync(SourceTarget, Small, Ct);
        Assert.Empty(missing.Slices);
        Assert.Empty(missing.Tables.Single().Columns);
    }

    [Fact]
    public async Task Plan_RefusesUnusableOptions()
    {
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() =>
            Runner().PlanAsync(SourceTarget, new ArchiveCopyOptions { TargetSliceRows = 0 }, Ct));
    }

    [Fact]
    public async Task DescribeAndVersion_ReadTheCatalog()
    {
        var table = await Runner().DescribeAsync(SourceTarget, ArchiveCopyTable.Data, Small, Ct);

        Assert.Equal(["timestamp", "rtid", "value"], table.Columns.Select(c => c.Name));
        Assert.Equal("timestamp with time zone", table.Columns[0].DataType);
        Assert.Equal("5.10.10", await Runner().GetStorageVersionAsync(Small, Ct));
    }

    // ---- COPY TO -------------------------------------------------------------------------------------------

    [Fact]
    public async Task CopyTo_SendsOneStatementPerSlice_IntoTheSliceFolder_AndReportsTheRows()
    {
        WithData();
        var runner = Runner();
        var plan = await runner.PlanAsync(SourceTarget, Small, Ct);

        var results = await Collect(runner.CopyToAsync(SourceTarget, plan, Location, Small, Ct));

        Assert.All(results, r => Assert.True(r.IsSuccess));
        Assert.Equal(plan.Slices, results.Select(r => r.Slice));
        Assert.Equal(plan.Slices.Select(s => s.RowCount), results.Select(r => r.CopiedRows));
        Assert.All(results, r => Assert.Null(r.Message));

        var copies = _crate.Executed.Where(s => s.StartsWith("COPY", StringComparison.Ordinal)).ToList();
        Assert.Equal(plan.Slices.Count, copies.Count);
        for (var i = 0; i < copies.Count; i++)
        {
            Assert.Contains($"/{Source}/data/{plan.Slices[i].Key}'", copies[i]);
            Assert.Contains("WITH (compression = 'gzip', protocol = 'http', sas_token = 'CANARYSAS')", copies[i]);
        }

        Assert.Equal(plan.Slices.Count, _crate.Store.Count);
    }

    [Fact]
    public async Task CopyTo_ADifferenceBetweenCountedAndCopiedRows_IsAWarningNotAFailure()
    {
        WithData();
        var runner = Runner();
        var plan = await runner.PlanAsync(SourceTarget, Small, Ct);
        _crate.SourceRows.Add(plan.Slices[0].FromUtc!.Value.AddMinutes(1)); // written while the dump runs

        var results = await Collect(runner.CopyToAsync(SourceTarget, plan, Location, Small, Ct));

        Assert.All(results, r => Assert.True(r.IsSuccess));
        Assert.Equal(1, results[0].RowCountDifference);
        Assert.Contains("Counted", results[0].Message);
    }

    [Fact]
    public async Task CopyTo_AFailingSlice_IsReportedWithoutCredential_AndEndsTheCopy_EarlierSlicesStayValid()
    {
        WithData();
        var runner = Runner();
        var plan = await runner.PlanAsync(SourceTarget, Small, Ct);
        var copies = 0;
        _crate.Fault = statement =>
        {
            if (statement.IsCopy && ++copies == 2)
            {
                throw new StreamDataCopyStatementException(statement.Redacted, "XX000",
                    $"Cannot access 's3://CANARYKEYID1234:{CanarySecret}@minio.octo-infra:9000/octo-artifacts/x' with token CANARYSAS");
            }
        };

        var results = await Collect(runner.CopyToAsync(SourceTarget, plan, Location, Small, Ct));

        Assert.Equal(2, results.Count);
        Assert.True(results[0].IsSuccess);
        Assert.False(results[1].IsSuccess);
        Assert.Contains("Cannot access", results[1].Message);
        Assert.DoesNotContain("CANARY", results[1].Message);
        Assert.Single(_crate.Store); // the third slice was never attempted
    }

    [Fact]
    public async Task CopyTo_ATimeout_IsAFailedSliceToo()
    {
        WithData();
        var runner = Runner();
        var plan = await runner.PlanAsync(SourceTarget, Small, Ct);
        _crate.Fault = statement =>
        {
            if (statement.IsCopy)
            {
                throw new StreamDataStatementTimeoutException(CrateStatementClass.Maintenance, TimeSpan.FromSeconds(1),
                    TimeSpan.FromSeconds(2), statement.Redacted, null);
            }
        };

        var results = await Collect(runner.CopyToAsync(SourceTarget, plan, Location, Small, Ct));

        var failed = Assert.Single(results);
        Assert.False(failed.IsSuccess);
        Assert.Contains("budget", failed.Message);
        Assert.DoesNotContain("CANARY", failed.Message);
    }

    [Fact]
    public async Task CopyTo_StopsBetweenSlices_WhenTheCallerCancels()
    {
        WithData();
        var runner = Runner();
        var plan = await runner.PlanAsync(SourceTarget, Small, Ct);
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(Ct);
        var seen = 0;

        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
        {
            await foreach (var _ in runner.CopyToAsync(SourceTarget, plan, Location, Small, cts.Token))
            {
                seen++;
                await cts.CancelAsync();
            }
        });

        Assert.Equal(1, seen);
        Assert.Single(_crate.Store);
    }

    [Fact]
    public async Task CopyTo_RefusesBeforeAnyCopy_WhenTheJobLogWouldRecordTheCredential()
    {
        WithData();
        var runner = Runner();
        var plan = await runner.PlanAsync(SourceTarget, Small, Ct);
        _crate.Filter = "true";

        var ex = await Assert.ThrowsAsync<ArchiveCopyRefusedException>(() =>
            Collect(runner.CopyToAsync(SourceTarget, plan, Location, Small, Ct)));

        Assert.Equal(ArchiveCopyRefusalReason.JobLogNotFiltered, ex.Reason);
        Assert.Contains("stats.jobs_log_filter", ex.Message);
        Assert.DoesNotContain(_crate.Executed, s => s.StartsWith("COPY", StringComparison.Ordinal));
    }

    [Fact]
    public async Task CopyTo_RefusesAPlanOfAnotherArchive_AndAGenerationMapSliceForAnArchiveWithoutAMap()
    {
        WithData();
        var runner = Runner();
        var plan = await runner.PlanAsync(SourceTarget, Small, Ct);

        await Assert.ThrowsAsync<ArgumentException>(() =>
            Collect(runner.CopyToAsync(SourceTarget, plan with { ArchiveRtId = Restored }, Location, Small, Ct)));
        // Since AB#6968 a rollup's map is copied; an archive that is not a rollup has none to copy.
        var refused = await Assert.ThrowsAsync<ArchiveCopyRefusedException>(() => Collect(runner.CopyToAsync(SourceTarget,
            new ArchiveCopyPlan(Source, [], [ArchiveCopySlice.ForGenerationMap(3)]), Location, Small, Ct)));
        Assert.Contains("not a rollup", refused.Message, StringComparison.Ordinal);
    }

    // ---- COPY FROM -----------------------------------------------------------------------------------------

    private async Task<ArchiveCopyPlan> Dumped(bool withBytes = false)
    {
        WithData();
        var runner = Runner();
        var plan = await runner.PlanAsync(SourceTarget, Small, Ct);
        await Collect(runner.CopyToAsync(SourceTarget, plan, Location, Small, Ct));
        _crate.Executed.Clear();
        _crate.Shown.Clear();
        return withBytes
            ? plan with { Slices = plan.Slices.Select(s => s.WithStagedBytes(1L << 30)).ToList() }
            : plan;
    }

    [Fact]
    public async Task CopyFrom_LoadsSliceBySlice_IntoAnotherArchive_ThenRestoresSettingsAndMerges()
    {
        var plan = await Dumped();

        var results = await Collect(Runner().CopyFromAsync(RestoreTarget, plan, Location, Small, Ct));

        Assert.All(results, r => Assert.True(r.IsSuccess));
        Assert.Equal(plan.Slices.Select(s => s.RowCount), results.Select(r => r.CopiedRows));
        Assert.All(results, r => Assert.Equal(0, r.ErrorRows));
        Assert.Equal(_crate.SourceRows.Order(), _crate.TargetRows.Order());

        var loads = _crate.Executed.Where(s => s.StartsWith("COPY", StringComparison.Ordinal)).ToList();
        Assert.Equal(plan.Slices.Count, loads.Count);
        Assert.All(loads, l => Assert.EndsWith("RETURN SUMMARY", l));
        Assert.All(loads, l => Assert.Contains($"/{Source}/data/", l)); // the source's folders, not the target's

        // bulk settings on while loading, back to what they were before the merge
        Assert.Equal(["0/0", "0-1/1000"], _crate.SettingsHistory);
        var lastLoad = _crate.Executed.FindLastIndex(s => s.StartsWith("COPY", StringComparison.Ordinal));
        var restore = _crate.Executed.FindIndex(s => s.Contains("refresh_interval = 1000", StringComparison.Ordinal));
        var optimize = _crate.Executed.FindIndex(s => s.StartsWith("OPTIMIZE TABLE", StringComparison.Ordinal));
        Assert.InRange(restore, lastLoad + 1, optimize - 1);
        Assert.Contains("max_num_segments = 1", _crate.Executed[optimize]);
    }

    [Fact]
    public async Task CopyFrom_WithoutOptimize_SendsNoMerge()
    {
        var plan = await Dumped();

        await Collect(Runner().CopyFromAsync(RestoreTarget, plan, Location, Small with { OptimizeAfterLoad = false }, Ct));

        Assert.DoesNotContain(_crate.Executed, s => s.StartsWith("OPTIMIZE", StringComparison.Ordinal));
    }

    [Fact]
    public async Task CopyFrom_IntoAnEmptyTable_SetsTheNullsOfAColumnWithADefaultToItsDefault()
    {
        // A dump with a column list writes explicit nulls that COPY FROM keeps; the load gives them the default.
        var plan = await Dumped();
        _crate.ColumnDefaults["rtcreationdatetime"] = "_cast(current_timestamp(3), 'timestamp with time zone')";
        _crate.ColumnDefaults["was_updated"] = "false";
        _crate.TargetNulls["rtcreationdatetime"] = 4;

        await Collect(Runner().CopyFromAsync(RestoreTarget, plan, Location, Small, Ct));

        // One UPDATE per data slice, each bounded by the slice's time range like its COPY.
        var dataSlices = plan.Slices.Where(s => s.Table == ArchiveCopyTable.Data).ToList();
        var updates = _crate.Executed.Where(s => s.StartsWith("UPDATE", StringComparison.Ordinal)).ToList();
        Assert.Equal(dataSlices.Count, updates.Count);
        var first = dataSlices[0];
        Assert.Equal($"UPDATE {RestoreTarget.QualifiedTable} SET \"rtcreationdatetime\" = " +
                     "_cast(current_timestamp(3), 'timestamp with time zone') WHERE \"rtcreationdatetime\" IS NULL " +
                     $"AND \"{RestoreTarget.TimeColumn}\" >= '{CopyStatement.FormatTs(first.FromUtc!.Value)}' " +
                     $"AND \"{RestoreTarget.TimeColumn}\" < '{CopyStatement.FormatTs(first.ToUtc!.Value)}'", updates[0]);
        Assert.DoesNotContain(updates, u => u.Contains("was_updated", StringComparison.Ordinal)); // no NULLs there
        // The fill runs after the data is loaded and refreshed, and the table is refreshed again before it is counted.
        var executed = _crate.Executed;
        var updateIndex = executed.IndexOf(updates[0]);
        Assert.Contains(executed.Take(updateIndex), s => s.StartsWith("COPY", StringComparison.Ordinal));
        Assert.Contains(executed.Skip(updateIndex), s => s.StartsWith("REFRESH TABLE", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("UPDATE")]
    [InlineData("column_default")]
    [InlineData("IS NULL")]
    [InlineData("REFRESH")]
    public async Task CopyFrom_AFillStatementThatFails_LeavesTheNulls_AndTheLoadSucceeds(string failing)
    {
        var plan = await Dumped();
        _crate.ColumnDefaults["rtcreationdatetime"] = "current_timestamp(3)";
        _crate.TargetNulls["rtcreationdatetime"] = 4;
        var filled = false;
        var refreshFailed = false;
        var faulted = false;
        _crate.Fault = statement =>
        {
            var sql = statement.Reveal();
            // "REFRESH": only the refresh after a successful fill, not the refresh of the load itself.
            var isFill = sql.StartsWith("UPDATE", StringComparison.Ordinal) ||
                         sql.Contains("column_default", StringComparison.Ordinal) ||
                         sql.Contains(" IS NULL", StringComparison.Ordinal) ||
                         (filled && !refreshFailed && sql.StartsWith("REFRESH", StringComparison.Ordinal));
            filled |= sql.StartsWith("UPDATE", StringComparison.Ordinal);
            refreshFailed |= isFill && failing == "REFRESH" && sql.StartsWith("REFRESH", StringComparison.Ordinal);
            if (isFill && sql.Contains(failing, StringComparison.Ordinal))
            {
                faulted = true;
                throw new StreamDataCopyStatementException("UPDATE t", "XX000", "statement failed (test)");
            }
        };

        var results = await Collect(Runner().CopyFromAsync(RestoreTarget, plan, Location, Small, Ct));

        Assert.True(faulted, $"the '{failing}' statement of the fill must have been sent and failed");
        Assert.NotEmpty(results);
        Assert.All(results, r => Assert.True(r.IsSuccess, r.Message));
    }

    [Fact]
    public async Task CopyFrom_IntoATableThatHeldRows_LeavesEveryNullAlone()
    {
        var plan = await Dumped();
        _crate.TargetRows.Add(new DateTime(2000, 1, 1, 0, 0, 0, DateTimeKind.Utc)); // a row this load did not write
        _crate.ColumnDefaults["rtcreationdatetime"] = "current_timestamp(3)";
        _crate.TargetNulls["rtcreationdatetime"] = 1;

        await Collect(Runner().CopyFromAsync(RestoreTarget, plan, Location, Small, Ct));

        Assert.DoesNotContain(_crate.Executed, s => s.StartsWith("UPDATE", StringComparison.Ordinal));
        Assert.DoesNotContain(_crate.Executed, s => s.Contains("column_default", StringComparison.Ordinal));
    }

    [Fact]
    public async Task CopyFrom_WithoutNulls_SendsNoUpdate()
    {
        var plan = await Dumped();
        _crate.ColumnDefaults["rtcreationdatetime"] = "current_timestamp(3)";

        await Collect(Runner().CopyFromAsync(RestoreTarget, plan, Location, Small, Ct));

        Assert.DoesNotContain(_crate.Executed, s => s.StartsWith("UPDATE", StringComparison.Ordinal));
    }

    [Fact]
    public async Task CopyFrom_ADumpedColumnTheTableLacks_StopsBeforeLoadingAndChangesNothing()
    {
        var plan = await Dumped();
        _crate.TargetColumns = ["timestamp", "rtid"]; // 'value' is missing

        var ex = await Assert.ThrowsAsync<ArchiveCopyRefusedException>(() =>
            Collect(Runner().CopyFromAsync(RestoreTarget, plan, Location, Small, Ct)));

        Assert.Equal(ArchiveCopyRefusalReason.ColumnMissing, ex.Reason);
        Assert.Contains("'value'", ex.Message);
        Assert.DoesNotContain(_crate.Executed, s => s.StartsWith("COPY", StringComparison.Ordinal));
        Assert.DoesNotContain(_crate.Executed, s => s.StartsWith("ALTER", StringComparison.Ordinal));
        Assert.Empty(_crate.TargetRows);
    }

    [Fact]
    public async Task CopyFrom_AnEarlierDumpWithTheColumnOfARemovedComputedColumn_IsRefusedNamingItAsALeftover()
    {
        var dumped = await Dumped();
        // A dump made before leftovers were left out: its data table still describes the removed computed column.
        var data = dumped.Tables.Single(t => t.Table == ArchiveCopyTable.Data);
        var plan = dumped with
        {
            Tables = dumped.Tables.Select(t => t.Table == ArchiveCopyTable.Data
                ? data with { Columns = [.. data.Columns, new ArchiveCopyColumn("tripled", "double precision")] }
                : t).ToList()
        };
        var target = RestoreTarget with
        {
            Ownership = new ArchiveColumnOwnership(["timestamp", "rtid", "value"], [], new HashSet<string> { "value" })
        };

        var ex = await Assert.ThrowsAsync<ArchiveCopyRefusedException>(() =>
            Collect(Runner().CopyFromAsync(target, plan, Location, Small, Ct)));

        Assert.Equal(ArchiveCopyRefusalReason.ColumnMissing, ex.Reason);
        Assert.Contains("'tripled'", ex.Message);
        Assert.Contains("left over from removed computed columns", ex.Message);
        Assert.DoesNotContain(_crate.Executed, s => s.StartsWith("COPY", StringComparison.Ordinal));
        Assert.Empty(_crate.TargetRows);
    }

    [Fact]
    public async Task CopyFrom_ADumpedColumnThatIsNoLeftover_IsRefusedWithoutTheLeftoverHint()
    {
        var plan = await Dumped();
        _crate.TargetColumns = ["timestamp", "rtid"]; // 'value' is missing, and it is an attribute of the type
        var target = RestoreTarget with
        {
            Ownership = new ArchiveColumnOwnership(["timestamp", "rtid"], [], new HashSet<string> { "value" })
        };

        var ex = await Assert.ThrowsAsync<ArchiveCopyRefusedException>(() =>
            Collect(Runner().CopyFromAsync(target, plan, Location, Small, Ct)));

        Assert.Contains("'value'", ex.Message);
        Assert.DoesNotContain("left over", ex.Message);
    }

    [Fact]
    public async Task Plan_LeavesTheLeftoversOfComputedColumnsOut_AndTheDumpNamesTheColumnsItCopies()
    {
        WithData();
        _crate.SourceColumns = ["timestamp", "rtid", "value", "tripled", "doubled__v1", "note"];
        _crate.ColumnTypes["value"] = "double precision";
        _crate.ColumnTypes["tripled"] = "double precision";
        _crate.ColumnTypes["doubled__v1"] = "double precision";
        var target = SourceTarget with
        {
            Ownership = new ArchiveColumnOwnership(["timestamp", "rtid", "value"], [], new HashSet<string> { "value" })
        };

        var plan = await Runner().PlanAsync(target, Small, Ct);

        // 'tripled' maps to no attribute, 'doubled__v1' is a versioned name: both are left out. 'note' is text and stays.
        Assert.Equal(["timestamp", "rtid", "value", "note"],
            plan.Tables.Single(t => t.Table == ArchiveCopyTable.Data).Columns.Select(c => c.Name));
        Assert.Equal(17, plan.TotalRows);
        var table = await Runner().DescribeAsync(target, ArchiveCopyTable.Data, Small, Ct);
        Assert.Equal(["timestamp", "rtid", "value", "note"], StagingCopyRunner.DumpedColumns(target, plan, table.Columns));
        Assert.Equal(["timestamp", "rtid", "value", "note"], StagingCopyRunner.DumpedColumns(target, plan, null));
        Assert.Null(StagingCopyRunner.DumpedColumns(SourceTarget, plan, table.Columns));
    }

    [Fact]
    public async Task CopyTo_AnOwnedTableWithoutLeftovers_DumpsTheWholeRow_SoTheLoadAppliesColumnDefaults()
    {
        // AB#9932: a column list writes a NULL as an explicit null that COPY FROM keeps; the whole row leaves it out and
        // the load gives the column its DEFAULT (rtcreationdatetime, was_updated; AB#5490, AB#6647).
        WithData();
        _crate.ColumnTypes["value"] = "double precision";
        var target = SourceTarget with
        {
            Ownership = new ArchiveColumnOwnership(["timestamp", "rtid", "value"], [], new HashSet<string> { "value" })
        };
        var runner = Runner();
        var plan = await runner.PlanAsync(target, Small, Ct);

        var results = await Collect(runner.CopyToAsync(target, plan, Location, Small, Ct));

        Assert.All(results, r => Assert.True(r.IsSuccess));
        var copies = _crate.Executed.Where(s => s.StartsWith("COPY", StringComparison.Ordinal)).ToList();
        Assert.NotEmpty(copies);
        Assert.All(copies, c => Assert.DoesNotContain("json_object", c));
        Assert.All(copies, c => Assert.StartsWith($"COPY {target.QualifiedTable} WHERE", c));
    }

    [Fact]
    public async Task CopyTo_AnOwnedTableWithLeftovers_NamesTheColumnsItCopies()
    {
        // AB#9837 stays: the leftovers of computed columns are left out, so the dump names the columns it copies.
        WithData();
        _crate.SourceColumns = ["timestamp", "rtid", "value", "tripled", "doubled__v1"];
        _crate.ColumnTypes["value"] = "double precision";
        _crate.ColumnTypes["tripled"] = "double precision";
        _crate.ColumnTypes["doubled__v1"] = "double precision";
        var target = SourceTarget with
        {
            Ownership = new ArchiveColumnOwnership(["timestamp", "rtid", "value"], [], new HashSet<string> { "value" })
        };
        var runner = Runner();
        var plan = await runner.PlanAsync(target, Small, Ct);

        var results = await Collect(runner.CopyToAsync(target, plan, Location, Small, Ct));

        Assert.All(results, r => Assert.True(r.IsSuccess));
        var copies = _crate.Executed.Where(s => s.StartsWith("COPY", StringComparison.Ordinal)).ToList();
        Assert.NotEmpty(copies);
        Assert.All(copies, c => Assert.StartsWith($"COPY {target.QualifiedTable} (\"timestamp\", \"rtid\", \"value\") WHERE", c));
        Assert.All(copies, c => Assert.Contains("json_object", c));
        Assert.All(copies, c => Assert.DoesNotContain("tripled", c));
    }

    [Fact]
    public async Task CopyFrom_NotEnoughDiskOnANode_RefusesWithTheSizes_BeforeChangingAnything()
    {
        var plan = await Dumped(withBytes: true);
        _crate.NodeDiskAvailable = [1L << 30, 8L << 30]; // the tight node has 1 GiB; the load needs more

        var ex = await Assert.ThrowsAsync<ArchiveCopyRefusedException>(() =>
            Collect(Runner().CopyFromAsync(RestoreTarget, plan, Location, Small, Ct)));

        Assert.Equal(ArchiveCopyRefusalReason.InsufficientDiskSpace, ex.Reason);
        // two nodes, one replica after the load (0-1): every node holds one copy of the data
        var expectedPerNode = plan.Slices.Sum(s => s.StagedBytes!.Value) * StagingCopyRunner.DiskSpaceFactor;
        Assert.Contains(FormattableString.Invariant($"{expectedPerNode / (double)(1L << 30):0.0} GiB per node"), ex.Message);
        Assert.Contains("node 'node0' has 1.0 GiB available and room for 921.6 MiB below the high disk watermark (90%)", ex.Message);
        Assert.Contains("node0", ex.Message);
        Assert.DoesNotContain(_crate.Executed, s => s.StartsWith("ALTER", StringComparison.Ordinal));
        Assert.Empty(_crate.TargetRows);
    }

    [Fact]
    public async Task CopyFrom_EnoughDisk_Loads()
    {
        var plan = await Dumped(withBytes: true);
        _crate.NodeDiskAvailable = [100L << 30, 100L << 30];

        var results = await Collect(Runner().CopyFromAsync(RestoreTarget, plan, Location, Small, Ct));

        Assert.All(results, r => Assert.True(r.IsSuccess));
    }

    [Fact]
    public async Task CopyFrom_AFailureInTheMiddle_EndsTheCopy_RestoresTheSettings_AndDoesNotMerge()
    {
        var plan = await Dumped();
        var loads = 0;
        _crate.Fault = statement =>
        {
            if (statement.IsCopy && ++loads == 2)
            {
                throw new StreamDataCopyStatementException(statement.Redacted, null, $"no access for CANARYKEYID1234:{CanarySecret}");
            }
        };

        var results = await Collect(Runner().CopyFromAsync(RestoreTarget, plan, Location, Small, Ct));

        Assert.Equal([ArchiveCopySliceStatus.Succeeded, ArchiveCopySliceStatus.Failed], results.Select(r => r.Status));
        Assert.DoesNotContain("CANARY", results[1].Message);
        Assert.Equal(["0/0", "0-1/1000"], _crate.SettingsHistory);
        Assert.DoesNotContain(_crate.Executed, s => s.StartsWith("OPTIMIZE", StringComparison.Ordinal));
        Assert.Equal(plan.Slices[0].RowCount, _crate.TargetRows.Count);
    }

    [Fact]
    public async Task CopyFrom_TheBulkSettingsStatementFails_StillPutsTheSettingsBack()
    {
        var plan = await Dumped();
        var alters = 0;
        _crate.Fault = statement =>
        {
            // The first ALTER is the one that switches to the bulk settings: it fails (e.g. a command timeout after the
            // server applied it), so the table may already sit on replicas 0 / refresh 0 without the runner having noticed.
            if (statement.Reveal().StartsWith("ALTER TABLE", StringComparison.Ordinal) && ++alters == 1)
            {
                throw new TimeoutException("the bulk settings statement timed out");
            }
        };

        await Assert.ThrowsAsync<TimeoutException>(() =>
            Collect(Runner().CopyFromAsync(RestoreTarget, plan, Location, Small, Ct)));

        // The restore ran with the values read before (the second ALTER) and nothing was loaded.
        Assert.Equal(2, alters);
        Assert.Equal(["0-1/1000"], _crate.SettingsHistory);
        Assert.Empty(_crate.TargetRows);
    }

    [Fact]
    public async Task CopyFrom_Cancelled_PutsTheSettingsBack()
    {
        var plan = await Dumped();
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(Ct);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
        {
            await foreach (var _ in Runner().CopyFromAsync(RestoreTarget, plan, Location, Small, cts.Token))
            {
                await cts.CancelAsync();
            }
        });

        Assert.Equal(["0/0", "0-1/1000"], _crate.SettingsHistory);
    }

    [Fact]
    public async Task CopyFrom_AbandonedByTheCaller_PutsTheSettingsBack()
    {
        var plan = await Dumped();

        await using (var enumerator = Runner().CopyFromAsync(RestoreTarget, plan, Location, Small, Ct).GetAsyncEnumerator(Ct))
        {
            Assert.True(await enumerator.MoveNextAsync());
            Assert.Equal("0/0", _crate.SettingsHistory.Single());
        }

        Assert.Equal(["0/0", "0-1/1000"], _crate.SettingsHistory);
    }

    [Fact]
    public async Task CopyFrom_PutsBackTheSettingsTheTableHadBefore()
    {
        var plan = await Dumped();
        _crate.Replicas = "1";
        _crate.Refresh = 5000;

        await Collect(Runner().CopyFromAsync(RestoreTarget, plan, Location, Small, Ct));

        Assert.Equal("1", _crate.Replicas);
        Assert.Equal(5000, _crate.Refresh);
    }

    [Fact]
    public async Task CopyFrom_RejectedRows_FailTheSlice_AndARerunWithOverwriteDuplicatesLeavesNoDuplicates()
    {
        var plan = await Dumped();
        var runner = Runner();
        await Collect(runner.CopyFromAsync(RestoreTarget, plan, Location, Small, Ct));
        var rowsAfterFirstLoad = _crate.TargetRows.Count;

        var rerun = await Collect(runner.CopyFromAsync(RestoreTarget, plan, Location, Small, Ct));
        var failed = Assert.Single(rerun);
        Assert.False(failed.IsSuccess);
        Assert.True(failed.ErrorRows > 0);
        Assert.Contains("rejected", failed.Message);

        var overwritten = await Collect(runner.CopyFromAsync(RestoreTarget, plan, Location, Small with { OverwriteDuplicates = true }, Ct));

        Assert.All(overwritten, r => Assert.True(r.IsSuccess));
        Assert.Equal(rowsAfterFirstLoad, _crate.TargetRows.Count);
        Assert.Equal(_crate.TargetRows.Count, _crate.TargetRows.Distinct().Count());
    }

    [Fact]
    public async Task CopyFrom_RefusesWithoutJobLogFilter_BeforeChangingAnything()
    {
        var plan = await Dumped();
        _crate.Filter = "true";

        await Assert.ThrowsAsync<ArchiveCopyRefusedException>(() =>
            Collect(Runner().CopyFromAsync(RestoreTarget, plan, Location, Small, Ct)));

        Assert.DoesNotContain(_crate.Executed, s => s.StartsWith("ALTER", StringComparison.Ordinal));
    }

    // ---- credential ----------------------------------------------------------------------------------------

    [Fact]
    public async Task EverythingThatMayBeShown_CarriesNoCredential_WhileTheExecutedStatementsDo()
    {
        WithData();
        var runner = Runner();
        var plan = await runner.PlanAsync(SourceTarget, Small, Ct);
        var dump = await Collect(runner.CopyToAsync(SourceTarget, plan, Location, Small, Ct));
        var load = await Collect(runner.CopyFromAsync(RestoreTarget, plan, Location, Small, Ct));

        var shown = _crate.Shown.Concat(dump.Concat(load).Select(r => r.Message ?? string.Empty)).ToList();
        Assert.All(shown, text =>
        {
            Assert.DoesNotContain("CANARY", text);
            Assert.DoesNotContain("SECRET+value", text);
        });
        Assert.Contains(_crate.Executed, s => s.Contains("CANARYKEYID1234", StringComparison.Ordinal));
        Assert.Contains(_crate.Shown, s => s.Contains("s3://***@minio.octo-infra:9000", StringComparison.Ordinal));
    }
}
