using FakeItEasy;
using Meshmakers.Octo.ConstructionKit.Contracts;
using Meshmakers.Octo.ConstructionKit.Contracts.DataTransferObjects;
using Meshmakers.Octo.ConstructionKit.Contracts.DependencyGraph;
using Meshmakers.Octo.ConstructionKit.Contracts.Services;
using Meshmakers.Octo.Runtime.Contracts.Formulas;
using Meshmakers.Octo.Runtime.Contracts.StreamData;
using Meshmakers.Octo.Runtime.Engine.CrateDb;
using Meshmakers.Octo.Runtime.Engine.CrateDb.Configuration;
using Meshmakers.Octo.Runtime.Engine.MongoDb.IntegrationTests.StagingCopy;
using Meshmakers.Octo.Runtime.Engine.StreamData;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace Meshmakers.Octo.Runtime.Engine.MongoDb.IntegrationTests.ComputedColumns;

/// <summary>
///     A native dump and restore round trip against a real CrateDB for an archive whose table holds physical columns its
///     definition no longer owns: the column of a removed computed column (with the versions of its formula changes) and
///     the earlier version of a computed column whose formula changed. The dump leaves them out, the restore provisions
///     the table from the definition (the active versioned name of a computed column included) and loads every row. A
///     column that is no leftover of a computed column - ingested data of the CK type the definition does not declare, or
///     a column of a type no computed column has - stays in the dump and the restore refuses it before loading anything.
/// </summary>
[Collection(CrateStagingCopyCollection.Name)]
public class ArchiveComputedColumnLeftoverBackupIntegrationTests(CrateStagingCopyFixture fixture)
{
    private static readonly RtCkId<CkTypeId> SensorType = new("Test", new CkTypeId("Sensor"));
    private static readonly DateTime FirstRow = new(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
    private const int Rows = 48;

    private static readonly IFormulaEngine Formula =
        new ServiceCollection().AddFormulaEngine().BuildServiceProvider().GetRequiredService<IFormulaEngine>();

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private sealed record Lane(
        string Tenant, string Table, OctoObjectId Archive, MemoryArchiveStore Store, CrateDbStreamDataRepository Repository,
        ArchiveLifecycleService Lifecycle);

    /// <summary>Test/Sensor with the Double attributes Value, Pin and Spare; Pin is Hidden when <paramref name="pinHidden" />.</summary>
    private static ICkCacheService SensorCache(Func<bool> pinHidden)
    {
        var typeId = new CkId<CkTypeId>("Test-1.0.0/Sensor-1");
        CkTypeAttributeGraph Attribute(string name, CkAttributeAccessDto access) => new(
            new CkId<CkAttributeId>($"Test-1.0.0/{name}-1"), name, null, AttributeValueTypesDto.Double, null, null, null,
            null, null, isOptional: true, description: null) { Access = access };
        CkTypeGraph Graph() => new(typeId, isAbstract: false, isFinal: false, isCollectionRoot: true, [], null, null, [], [],
            new[]
            {
                Attribute("Value", CkAttributeAccessDto.ReadWrite),
                Attribute("Pin", pinHidden() ? CkAttributeAccessDto.Hidden : CkAttributeAccessDto.ReadWrite),
                Attribute("Spare", CkAttributeAccessDto.ReadWrite)
            }.ToDictionary(a => a.CkAttributeId, a => a),
            [], new CkGraphDirectedAssociations([]), "sensor", enableChangeStreamPreAndPostImages: false);

        var cache = A.Fake<ICkCacheService>();
        A.CallTo(() => cache.GetRtCkType(A<string>._, SensorType)).ReturnsLazily(Graph);
        CkTypeGraph? ignored;
        A.CallTo(() => cache.TryGetRtCkType(A<string>._, SensorType, out ignored))
            .ReturnsLazily(() => true).AssignsOutAndRefParameters(Graph());
        return cache;
    }

    private Lane NewLane(string tenant, OctoObjectId archive, MemoryArchiveStore store, ICkCacheService cache)
    {
        var configuration = Options.Create(new StreamDataConfiguration { ConnectionString = fixture.ConnectionString });
        var repository = new CrateDbStreamDataRepository(NullLogger<CrateDbStreamDataRepository>.Instance, cache,
            fixture.Client, fixture.Client, configuration, tenant, store, Formula);
        var lifecycle = new ArchiveLifecycleService(tenant, store, repository, A.Fake<IArchiveAuditTrail>(),
            NullLogger<ArchiveLifecycleService>.Instance, recomputeJobStore: new MemoryJobStore(), ckCacheService: cache);
        return new Lane(tenant, TenantSchema.QualifiedArchiveTable(tenant, archive.ToString()), archive, store, repository,
            lifecycle);
    }

    /// <summary>
    ///     The source: an archive with the columns <paramref name="paths" />, provisioned by the repository like an
    ///     activation does, with <see cref="Rows" /> hourly rows (Value = i * 1.5, Pin = 1234).
    /// </summary>
    private async Task<Lane> NewSourceAsync(ICkCacheService cache, params string[] paths)
    {
        var archive = OctoObjectId.GenerateNewId();
        var store = new MemoryArchiveStore();
        var snapshot = new ArchiveSnapshot(archive, SensorType, CkArchiveStatus.Activated, "meter",
            paths.Select(p => new CkArchiveColumnSpec(p, Indexed: false, Required: false)).ToList());
        store.Put(snapshot);
        var lane = NewLane(CrateStagingCopyFixture.NewTenantId(), archive, store, cache);
        await lane.Repository.EnsureArchiveCreatedAsync(snapshot);
        await lane.Repository.InsertAsync(archive, Enumerable.Range(0, Rows).Select(i => new StreamDataPoint
        {
            RtId = OctoObjectId.GenerateNewId(), CkTypeId = SensorType, Timestamp = FirstRow.AddHours(i),
            Attributes = paths.ToDictionary(p => p, p => (object?)(p == "Pin" ? 1234.0 : i * 1.5))
        }).ToList());
        await fixture.SqlAsync($"REFRESH TABLE {lane.Table}");
        return lane;
    }

    /// <summary>Dumps <paramref name="lane" /> natively into a fresh file location; returns the dump and the staged JSON lines.</summary>
    private async Task<(ArchiveCopyDumpResult Dump, ArchiveCopyStagingLocation Location, string Staged)> DumpAsync(Lane lane)
    {
        var location = CrateStagingCopyFixture.NewFileLocation();
        var options = new ArchiveCopyOptions { TargetSliceRows = 20 };
        var plan = await lane.Repository.PlanArchiveCopyAsync(lane.Archive, options, Ct);
        await fixture.PrepareDirectoriesAsync(plan, location);
        var dump = await lane.Repository.DumpArchiveToStagingAsync(lane.Archive, location, options, Ct);
        Assert.All(dump.Slices, s => Assert.True(s.IsSuccess, s.Message));
        var (exit, staged) = await fixture.ExecAsync("sh", "-c",
            $"find {CrateStagingCopyFixture.Path(location)} -name '*.json.gz' -exec zcat {{}} +");
        Assert.True(exit == 0, staged);
        return (dump, location, staged);
    }

    /// <summary>The restore: the definition of the source in a new tenant, its table provisioned from the definition.</summary>
    private async Task<Lane> NewTargetAsync(Lane source, ICkCacheService cache)
    {
        var definition = (await source.Store.GetAsync(source.Archive))!;
        var store = new MemoryArchiveStore();
        store.Put(definition);
        var target = NewLane(CrateStagingCopyFixture.NewTenantId(), source.Archive, store, cache);
        await target.Repository.EnsureArchiveCreatedAsync(definition);
        return target;
    }

    private async Task<List<string>> ColumnsAsync(Lane lane)
    {
        var rows = await fixture.QueryAsync(
            "SELECT column_name FROM information_schema.columns " +
            $"WHERE table_schema = '{TenantSchema.SchemaName(lane.Tenant)}' " +
            $"AND table_name = '{TenantSchema.ArchiveTableName(lane.Archive.ToString())}'");
        return rows.Select(r => r["column_name"]!.ToString()!).ToList();
    }

    private static List<string> Described(ArchiveCopyDumpResult dump)
        => dump.Plan.Tables.Single(t => t.Table == ArchiveCopyTable.Data).Columns.Select(c => c.Name).ToList();

    private static void AssertStagedWithout(string staged, params string[] columns)
    {
        var lines = staged.Split('\n', StringSplitOptions.RemoveEmptyEntries);
        Assert.Equal(Rows, lines.Length);
        Assert.All(lines, l => Assert.Contains("\"value\"", l, StringComparison.Ordinal));
        foreach (var column in columns)
        {
            Assert.All(lines, l => Assert.DoesNotContain($"\"{column}\"", l, StringComparison.Ordinal));
        }
    }

    /// <summary>The dump describes every column of the source table except <paramref name="leftOut" /> (no platform column lost).</summary>
    private async Task AssertDumpsEveryColumnButAsync(Lane source, ArchiveCopyDumpResult dump, params string[] leftOut)
        => Assert.Equal((await ColumnsAsync(source)).Except(leftOut).Order(StringComparer.Ordinal),
            Described(dump).Order(StringComparer.Ordinal));

    /// <summary>The value of the column Value in a query row (the key's case follows the read path).</summary>
    private static double ValueOf(IReadOnlyDictionary<string, object?> values)
        => Convert.ToDouble(Assert.Single(values, kv => kv.Key.Equals("Value", StringComparison.OrdinalIgnoreCase)).Value
                            ?? throw new InvalidOperationException("Value is null"));

    private async Task AssertRestoredAsync(Lane target, ArchiveCopyDumpResult dump, ArchiveCopyStagingLocation location)
    {
        var load = await target.Repository.LoadArchiveFromStagingAsync(target.Archive, dump.Plan, location, null, Ct);
        Assert.True(load.IsSuccess, string.Join("; ", load.Slices.Select(s => s.Message)));
        Assert.True(load.RowCountVerified);
        var history = await target.Repository.ExecuteQueryAsync(target.Archive,
            StreamDataQueryOptions.Create().WithCkTypeId(SensorType).WithColumns(["Value"])
                .WithTimeRange(FirstRow, FirstRow.AddDays(3)));
        Assert.Equal(Rows, history.Rows.Count);
        Assert.Equal(Enumerable.Range(0, Rows).Select(i => i * 1.5),
            history.Rows.Select(r => ValueOf(r.Values)).Order());

        // The restored table takes new rows (every computed column of the definition has its physical column).
        await target.Repository.InsertAsync(target.Archive, new StreamDataPoint
        {
            RtId = OctoObjectId.GenerateNewId(), CkTypeId = SensorType, Timestamp = FirstRow.AddDays(5),
            Attributes = new Dictionary<string, object?> { ["Value"] = 7.5 }
        });
        await fixture.SqlAsync($"REFRESH TABLE {target.Table}");
        Assert.Equal(Rows + 1, await fixture.CountAsync($"SELECT count(*) FROM {target.Table}"));
    }

    [Fact]
    public async Task RemovedComputedColumn_IsLeftOutOfTheDump_AndTheRestoreLoadsEveryRow()
    {
        var cache = SensorCache(() => false);
        var source = await NewSourceAsync(cache, "Value");
        await source.Lifecycle.AddComputedColumnAsync(source.Archive, "doubled", "value * 2", FormulaResultType.Double,
            indexed: false);
        await source.Lifecycle.RemoveComputedColumnAsync(source.Archive, "doubled");
        Assert.Contains("doubled", await ColumnsAsync(source)); // the physical column stays behind

        var (dump, location, staged) = await DumpAsync(source);

        Assert.Equal(Rows, dump.Plan.TotalRows);
        Assert.DoesNotContain("doubled", Described(dump));
        Assert.Contains("value", Described(dump));
        AssertStagedWithout(staged, "doubled");
        await AssertDumpsEveryColumnButAsync(source, dump, "doubled");

        var target = await NewTargetAsync(source, cache);
        Assert.DoesNotContain("doubled", await ColumnsAsync(target));
        await AssertRestoredAsync(target, dump, location);
    }

    [Fact]
    public async Task RemovedComputedColumnWithAFormulaChange_LeavesEveryVersionOut_AndTheRestoreLoadsEveryRow()
    {
        var cache = SensorCache(() => false);
        var source = await NewSourceAsync(cache, "Value");
        await source.Lifecycle.AddComputedColumnAsync(source.Archive, "doubled", "value * 2", FormulaResultType.Double,
            indexed: false);
        await source.Lifecycle.UpdateComputedColumnFormulaAsync(source.Archive, "doubled", "value * 3");
        await source.Lifecycle.RemoveComputedColumnAsync(source.Archive, "doubled");
        var physical = await ColumnsAsync(source);
        Assert.Contains("doubled", physical);
        Assert.Contains("doubled__v1", physical);

        var (dump, location, staged) = await DumpAsync(source);

        Assert.DoesNotContain(Described(dump), c => c.StartsWith("doubled", StringComparison.Ordinal));
        AssertStagedWithout(staged, "doubled", "doubled__v1");
        await AssertDumpsEveryColumnButAsync(source, dump, "doubled", "doubled__v1");

        var target = await NewTargetAsync(source, cache);
        await AssertRestoredAsync(target, dump, location);
    }

    [Fact]
    public async Task ComputedColumnWithAFormulaChange_RoundTripsItsActiveVersion_AndLeavesTheEarlierOneOut()
    {
        var cache = SensorCache(() => false);
        var source = await NewSourceAsync(cache, "Value");
        await source.Lifecycle.AddComputedColumnAsync(source.Archive, "doubled", "value * 2", FormulaResultType.Double,
            indexed: false);
        await source.Lifecycle.UpdateComputedColumnFormulaAsync(source.Archive, "doubled", "value * 3");
        Assert.Equal(1, (await source.Store.GetAsync(source.Archive))!.Columns.Single(c => c.IsComputed).ComputedVersion);

        var (dump, location, staged) = await DumpAsync(source);

        Assert.Contains("doubled__v1", Described(dump));
        Assert.DoesNotContain("doubled", Described(dump));
        AssertStagedWithout(staged, "doubled");
        await AssertDumpsEveryColumnButAsync(source, dump, "doubled");
        Assert.Contains("\"doubled__v1\"", staged, StringComparison.Ordinal);

        var target = await NewTargetAsync(source, cache);
        var provisioned = await ColumnsAsync(target);
        Assert.Contains("doubled__v1", provisioned);
        Assert.DoesNotContain("doubled", provisioned);
        await AssertRestoredAsync(target, dump, location);

        // The restored values are the ones of the active formula, read by the column's logical name.
        var restored = await target.Repository.ExecuteQueryAsync(target.Archive,
            StreamDataQueryOptions.Create().WithCkTypeId(SensorType).WithColumns(["Value", "doubled"])
                .WithTimeRange(FirstRow, FirstRow.AddHours(Rows)));
        Assert.Equal(Rows, restored.Rows.Count);
        Assert.Equal(Enumerable.Range(0, Rows).Select(i => i * 1.5), restored.Rows.Select(r => ValueOf(r.Values)).Order());
        Assert.All(restored.Rows, r => Assert.NotNull(r.Values["doubled"]));
        Assert.All(restored.Rows, r => Assert.Equal(ValueOf(r.Values) * 3, Convert.ToDouble(r.Values["doubled"]), 6));
    }

    [Fact]
    public async Task HiddenColumnAndRemovedComputedColumn_AreBothLeftOut_AndTheRestoreLoadsEveryRow()
    {
        var pinHidden = false;
        var cache = SensorCache(() => pinHidden);
        var source = await NewSourceAsync(cache, "Value", "Pin");
        await source.Lifecycle.AddComputedColumnAsync(source.Archive, "pinDoubled", "pin * 2", FormulaResultType.Double,
            indexed: false);
        await source.Lifecycle.AddComputedColumnAsync(source.Archive, "tripled", "value * 3", FormulaResultType.Double,
            indexed: false);
        await source.Lifecycle.RemoveComputedColumnAsync(source.Archive, "tripled");
        pinHidden = true;
        Assert.False(await source.Lifecycle.RevalidateAccessAsync(source.Archive));
        Assert.True((await source.Store.GetAsync(source.Archive))!.IsFailedOnHiddenColumns);

        var (dump, location, staged) = await DumpAsync(source);

        var described = Described(dump);
        Assert.DoesNotContain(described, c => c.StartsWith("pin", StringComparison.Ordinal));
        Assert.DoesNotContain("tripled", described);
        AssertStagedWithout(staged, "pin", "pindoubled", "tripled");
        await AssertDumpsEveryColumnButAsync(source, dump, "pin", "pindoubled", "tripled");
        Assert.DoesNotContain("1234", staged, StringComparison.Ordinal);

        var target = await NewTargetAsync(source, cache);
        var load = await target.Repository.LoadArchiveFromStagingAsync(target.Archive, dump.Plan, location, null, Ct);
        Assert.True(load.IsSuccess, string.Join("; ", load.Slices.Select(s => s.Message)));
        Assert.True(load.RowCountVerified);
        Assert.Equal(Rows, await fixture.CountAsync($"SELECT count(*) FROM {target.Table}"));
        var restored = await fixture.QueryAsync($"SELECT \"value\" FROM {target.Table} ORDER BY \"timestamp\"");
        Assert.Equal(Enumerable.Range(0, Rows).Select(i => i * 1.5), restored.Select(r => Convert.ToDouble(r["value"])));
    }

    [Fact]
    public async Task IngestedColumnTheDefinitionDoesNotDeclare_StaysInTheDump_AndTheRestoreRefusesBeforeLoading()
    {
        var cache = SensorCache(() => false);
        var source = await NewSourceAsync(cache, "Value");
        await source.Lifecycle.AddComputedColumnAsync(source.Archive, "doubled", "value * 2", FormulaResultType.Double,
            indexed: false);
        await source.Lifecycle.RemoveComputedColumnAsync(source.Archive, "doubled");
        // Spare is an attribute of the type: its column holds ingested data, never derived values.
        await fixture.SqlAsync($"ALTER TABLE {source.Table} ADD COLUMN \"spare\" DOUBLE PRECISION");
        await fixture.SqlAsync($"UPDATE {source.Table} SET \"spare\" = 42.0");
        await fixture.SqlAsync($"REFRESH TABLE {source.Table}");

        var (dump, location, _) = await DumpAsync(source);
        Assert.Contains("spare", Described(dump));
        Assert.DoesNotContain("doubled", Described(dump));

        var target = await NewTargetAsync(source, cache);
        var ex = await Assert.ThrowsAsync<ArchiveCopyRefusedException>(() =>
            target.Repository.LoadArchiveFromStagingAsync(target.Archive, dump.Plan, location, null, Ct));

        Assert.Equal(ArchiveCopyRefusalReason.ColumnMissing, ex.Reason);
        Assert.Contains("'spare'", ex.Message, StringComparison.Ordinal);
        Assert.Equal(0, await fixture.CountAsync($"SELECT count(*) FROM {target.Table}"));
    }

    [Fact]
    public async Task ColumnOfATypeNoComputedColumnHas_StaysInTheDump_AndTheRestoreRefusesBeforeLoading()
    {
        var cache = SensorCache(() => false);
        var source = await NewSourceAsync(cache, "Value");
        await fixture.SqlAsync($"ALTER TABLE {source.Table} ADD COLUMN \"note\" TEXT");
        await fixture.SqlAsync($"UPDATE {source.Table} SET \"note\" = 'kept'");
        await fixture.SqlAsync($"REFRESH TABLE {source.Table}");

        var (dump, location, _) = await DumpAsync(source);
        Assert.Contains("note", Described(dump));

        var target = await NewTargetAsync(source, cache);
        var ex = await Assert.ThrowsAsync<ArchiveCopyRefusedException>(() =>
            target.Repository.LoadArchiveFromStagingAsync(target.Archive, dump.Plan, location, null, Ct));

        Assert.Equal(ArchiveCopyRefusalReason.ColumnMissing, ex.Reason);
        Assert.Contains("'note'", ex.Message, StringComparison.Ordinal);
        Assert.Equal(0, await fixture.CountAsync($"SELECT count(*) FROM {target.Table}"));
    }

    [Fact]
    public async Task RollupWithTheColumnOfARemovedComputedColumn_LeavesItOut_AndTheRestoreLoadsDataAndMap()
    {
        var tenant = CrateStagingCopyFixture.NewTenantId();
        var source = OctoObjectId.GenerateNewId();
        var target = OctoObjectId.GenerateNewId();
        await fixture.CreateRollupAsync(tenant, source, generations: 2);
        await fixture.CreateRollupAsync(tenant, target);
        var sourceTable = TenantSchema.QualifiedArchiveTable(tenant, source.ToString());
        // A rollup-internal computed column that was removed: its column stays in the table.
        await fixture.SqlAsync($"ALTER TABLE {sourceTable} ADD COLUMN \"ratio\" DOUBLE PRECISION");
        await fixture.SqlAsync($"UPDATE {sourceTable} SET \"ratio\" = 0.5");
        await fixture.SqlAsync($"REFRESH TABLE {sourceTable}");
        var sourceRepo = fixture.NewRollupRepository(tenant, source);
        var targetRepo = fixture.NewRollupRepository(tenant, target);
        var location = CrateStagingCopyFixture.NewFileLocation();
        var plan = await sourceRepo.PlanArchiveCopyAsync(source, new ArchiveCopyOptions { TargetSliceRows = 200 }, Ct);
        await fixture.PrepareDirectoriesAsync(plan, location);

        var dump = await sourceRepo.DumpArchiveToStagingAsync(source, location, new ArchiveCopyOptions { TargetSliceRows = 200 }, Ct);

        Assert.All(dump.Slices, s => Assert.True(s.IsSuccess, s.Message));
        Assert.DoesNotContain("ratio", Described(dump));
        var sourceColumns = await fixture.QueryAsync(
            "SELECT column_name FROM information_schema.columns " +
            $"WHERE table_schema = '{TenantSchema.SchemaName(tenant)}' AND table_name = '{TenantSchema.ArchiveTableName(source.ToString())}'");
        Assert.Equal(sourceColumns.Select(r => r["column_name"]!.ToString()!).Where(c => c != "ratio").Order(StringComparer.Ordinal),
            Described(dump).Order(StringComparer.Ordinal));
        var load = await targetRepo.LoadArchiveFromStagingAsync(target, dump.Plan, location, null, Ct);
        Assert.True(load.IsSuccess, string.Join("; ", load.Slices.Select(s => s.Message)));
        Assert.True(load.RowCountVerified);
    }

    [Fact]
    public async Task FreshlyProvisionedTimeRangeArchive_DumpsEveryColumnItsDefinitionProvisions()
    {
        // A definition restored in the middle of a formula change: active version 1, pending version 2.
        var archive = OctoObjectId.GenerateNewId();
        var definition = new ArchiveSnapshot(archive, SensorType, CkArchiveStatus.Activated, "tr",
            new List<CkArchiveColumnSpec>
            {
                new("Value", Indexed: false, Required: false),
                new(string.Empty, Indexed: false, Required: false)
                {
                    Name = "doubled", Formula = "value * 2", ResultType = FormulaResultType.Double,
                    ComputedState = ComputedColumnState.Active, ComputedVersion = 1, PendingFormula = "value * 4"
                },
                new(string.Empty, Indexed: false, Required: false)
                {
                    Name = "positive", Formula = "value > 0", ResultType = FormulaResultType.Boolean,
                    ComputedState = ComputedColumnState.Active
                }
            })
        {
            IsTimeRange = true,
            Period = TimeSpan.FromHours(1)
        };
        var store = new MemoryArchiveStore();
        store.Put(definition);
        var lane = NewLane(CrateStagingCopyFixture.NewTenantId(), archive, store, SensorCache(() => false));
        await lane.Repository.EnsureArchiveCreatedAsync(definition);
        await fixture.SqlAsync(
            $"INSERT INTO {lane.Table} (\"window_start\", \"window_end\", \"rtid\", \"cktypeid\", \"value\", \"doubled__v1\", " +
            "\"doubled__v2\", \"positive\") " +
            "SELECT CAST(1767225600000 + CAST(generate_series AS BIGINT) * 3600000 AS TIMESTAMP WITH TIME ZONE), " +
            "CAST(1767225600000 + (CAST(generate_series AS BIGINT) + 1) * 3600000 AS TIMESTAMP WITH TIME ZONE), 'rt1', 'Test/Sensor-1', " +
            $"generate_series * 1.5, generate_series * 3.0, generate_series * 6.0, generate_series > 0 FROM generate_series(0, {Rows - 1})");
        await fixture.SqlAsync($"REFRESH TABLE {lane.Table}");
        var provisioned = await ColumnsAsync(lane);
        Assert.Contains("doubled__v1", provisioned);
        Assert.Contains("doubled__v2", provisioned);
        Assert.Contains("positive", provisioned);
        Assert.DoesNotContain("doubled", provisioned);

        var (dump, location, _) = await DumpAsync(lane);

        await AssertDumpsEveryColumnButAsync(lane, dump);
        var target = await NewTargetAsync(lane, SensorCache(() => false));
        var load = await target.Repository.LoadArchiveFromStagingAsync(target.Archive, dump.Plan, location, null, Ct);
        Assert.True(load.IsSuccess, string.Join("; ", load.Slices.Select(s => s.Message)));
        Assert.True(load.RowCountVerified);
    }

    [Fact]
    public async Task ArchiveWithoutLeftovers_DumpsTheWholeRow_AndTheRestoreGivesANullTheColumnDefault()
    {
        // AB#9932: without a column left out the dump copies the whole row, which leaves a NULL out; the load then gives the
        // column its DEFAULT (rtcreationdatetime: CURRENT_TIMESTAMP), as documented for the native copy (AB#5490, AB#6647).
        // A column list would write an explicit null that the load keeps.
        var cache = SensorCache(() => false);
        var source = await NewSourceAsync(cache, "Value");
        await fixture.SqlAsync($"UPDATE {source.Table} SET \"rtcreationdatetime\" = NULL WHERE \"value\" < 15");
        await fixture.SqlAsync($"REFRESH TABLE {source.Table}");
        Assert.Equal(10, await fixture.CountAsync($"SELECT count(*) FROM {source.Table} WHERE \"rtcreationdatetime\" IS NULL"));

        var (dump, location, staged) = await DumpAsync(source);

        await AssertDumpsEveryColumnButAsync(source, dump);
        Assert.DoesNotContain("\"rtcreationdatetime\":null", staged, StringComparison.Ordinal);
        var target = await NewTargetAsync(source, cache);
        await AssertRestoredAsync(target, dump, location);
        Assert.Equal(0, await fixture.CountAsync($"SELECT count(*) FROM {target.Table} WHERE \"rtcreationdatetime\" IS NULL"));
    }

    [Fact]
    public async Task ArchiveWithALeftover_NamesTheColumnsItDumps_AndStillLeavesTheLeftoverOut()
    {
        // AB#9837 stays with AB#9932: a leftover of a computed column is left out, so the dump names the columns it copies.
        var cache = SensorCache(() => false);
        var source = await NewSourceAsync(cache, "Value");
        await source.Lifecycle.AddComputedColumnAsync(source.Archive, "doubled", "value * 2", FormulaResultType.Double,
            indexed: false);
        await source.Lifecycle.RemoveComputedColumnAsync(source.Archive, "doubled");
        await fixture.SqlAsync($"UPDATE {source.Table} SET \"rtcreationdatetime\" = NULL, \"rtchangeddatetime\" = NULL " +
                               "WHERE \"value\" < 15");
        // The other rows carry a fixed past timestamp, so the restore shows that it keeps them and fills only the NULLs.
        await fixture.SqlAsync($"UPDATE {source.Table} SET \"rtcreationdatetime\" = '2020-01-01T00:00:00Z' " +
                               "WHERE \"value\" >= 15");
        await fixture.SqlAsync($"REFRESH TABLE {source.Table}");
        var kept = await fixture.CountAsync($"SELECT count(*) FROM {source.Table} WHERE \"value\" >= 15");
        Assert.True(kept > 0);

        var (dump, location, staged) = await DumpAsync(source);

        AssertStagedWithout(staged, "doubled");
        // The column list (json_object) writes a NULL as an explicit null - the whole row would leave it out.
        Assert.Contains("\"rtcreationdatetime\":null", staged, StringComparison.Ordinal);
        Assert.Contains("\"rtchangeddatetime\":null", staged, StringComparison.Ordinal);
        var target = await NewTargetAsync(source, cache);
        Assert.DoesNotContain("doubled", await ColumnsAsync(target));
        var load = await target.Repository.LoadArchiveFromStagingAsync(target.Archive, dump.Plan, location, null, Ct);
        Assert.True(load.IsSuccess, string.Join("; ", load.Slices.Select(s => s.Message)));
        Assert.True(load.RowCountVerified);
        // AB#9933: after the load into the empty table every NULL of a column with a DEFAULT gets that default, as the
        // whole-row dump above gives it - the explicit null of the column list no longer stays NULL.
        await fixture.SqlAsync($"REFRESH TABLE {target.Table}");
        Assert.Equal(0, await fixture.CountAsync($"SELECT count(*) FROM {target.Table} WHERE \"rtcreationdatetime\" IS NULL"));
        Assert.Equal(0, await fixture.CountAsync($"SELECT count(*) FROM {target.Table} WHERE \"rtchangeddatetime\" IS NULL"));
        Assert.Equal(await fixture.CountAsync($"SELECT count(*) FROM {source.Table}"),
            await fixture.CountAsync($"SELECT count(*) FROM {target.Table}"));
        // The filled rows got the default (the time of the load), the others keep their source value.
        Assert.Equal(10, await fixture.CountAsync(
            $"SELECT count(*) FROM {target.Table} WHERE \"value\" < 15 AND \"rtcreationdatetime\" > '2020-01-02T00:00:00Z'"));
        Assert.Equal(kept, await fixture.CountAsync(
            $"SELECT count(*) FROM {target.Table} WHERE \"value\" >= 15 AND \"rtcreationdatetime\" = '2020-01-01T00:00:00Z'"));
    }
}
