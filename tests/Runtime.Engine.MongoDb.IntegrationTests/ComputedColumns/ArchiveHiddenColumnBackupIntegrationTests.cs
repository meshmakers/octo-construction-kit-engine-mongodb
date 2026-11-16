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
///     AB#9141 against a real CrateDB: a tenant backup of an archive whose column <c>Pin</c> reaches a Hidden attribute
///     carries no <c>Pin</c> values - neither the staged files of the native copy (<c>COPY TO</c>) nor its manifest
///     description nor the row export - and neither do the computed columns derived from <c>Pin</c>. The restore
///     provisions the archive Failed on Hidden columns without <c>Pin</c>, loads every row, keeps it Failed, and the
///     redaction resumes it.
/// </summary>
[Collection(CrateStagingCopyCollection.Name)]
public class ArchiveHiddenColumnBackupIntegrationTests(CrateStagingCopyFixture fixture)
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

    /// <summary>Test/Sensor with the Double attributes Value and Pin; Pin is Hidden when <paramref name="pinHidden" />.</summary>
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
                Attribute("Pin", pinHidden() ? CkAttributeAccessDto.Hidden : CkAttributeAccessDto.ReadWrite)
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

    /// <summary>The source: an activated archive with Value and Pin, <see cref="Rows" /> hourly rows written while Pin was visible.</summary>
    private async Task<Lane> NewSourceAsync(ICkCacheService cache)
    {
        var tenant = CrateStagingCopyFixture.NewTenantId();
        var archive = OctoObjectId.GenerateNewId();
        await fixture.CreateRawArchiveAsync(tenant, archive);
        var store = new MemoryArchiveStore();
        store.Put(new ArchiveSnapshot(archive, SensorType, CkArchiveStatus.Activated, "meter",
            new List<CkArchiveColumnSpec> { new("Value", Indexed: false, Required: false), new("Pin", Indexed: false, Required: false) }));
        var lane = NewLane(tenant, archive, store, cache);
        await fixture.SqlAsync($"ALTER TABLE {lane.Table} ADD COLUMN \"pin\" DOUBLE PRECISION");
        await lane.Repository.InsertAsync(archive, Enumerable.Range(0, Rows).Select(i => new StreamDataPoint
        {
            RtId = OctoObjectId.GenerateNewId(), CkTypeId = SensorType, Timestamp = FirstRow.AddHours(i),
            Attributes = new Dictionary<string, object?> { ["Value"] = i * 1.5, ["Pin"] = 1234.0 }
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
        var (exit, staged) = await fixture.ExecAsync("sh", "-c",
            $"find {CrateStagingCopyFixture.Path(location)} -name '*.json.gz' -exec zcat {{}} +");
        Assert.True(exit == 0, staged);
        return (dump, location, staged);
    }

    private async Task<List<string>> ColumnsAsync(Lane lane)
    {
        var rows = await fixture.QueryAsync(
            "SELECT column_name FROM information_schema.columns " +
            $"WHERE table_schema = '{TenantSchema.SchemaName(lane.Tenant)}' " +
            $"AND table_name = '{TenantSchema.ArchiveTableName(lane.Archive.ToString())}'");
        return rows.Select(r => r["column_name"]!.ToString()!).ToList();
    }

    private static void AssertNoPin(string staged)
    {
        var lines = staged.Split('\n', StringSplitOptions.RemoveEmptyEntries);
        Assert.Equal(Rows, lines.Length);
        Assert.All(lines, l => Assert.Contains("\"value\"", l, StringComparison.Ordinal));
        Assert.All(lines, l => Assert.DoesNotContain("\"pin", l, StringComparison.OrdinalIgnoreCase));
        Assert.All(lines, l => Assert.DoesNotContain("1234", l, StringComparison.Ordinal));
    }

    [Fact]
    public async Task FailedOnHiddenColumns_DumpLeavesPinAndItsComputedColumnOut_RestoreKeepsItFailedWithEveryRow()
    {
        var pinHidden = false;
        var cache = SensorCache(() => pinHidden);
        var source = await NewSourceAsync(cache);
        await source.Lifecycle.AddComputedColumnAsync(source.Archive, "pinDoubled", "pin * 2", FormulaResultType.Double,
            indexed: false);
        await source.Lifecycle.AddComputedColumnAsync(source.Archive, "valueDoubled", "value * 2", FormulaResultType.Double,
            indexed: false);
        pinHidden = true;
        Assert.False(await source.Lifecycle.RevalidateAccessAsync(source.Archive));
        var failed = (await source.Store.GetAsync(source.Archive))!;
        Assert.True(failed.IsFailedOnHiddenColumns);

        var (dump, location, staged) = await DumpAsync(source);

        // The staging copy and the manifest description carry neither Pin nor the computed column over it.
        Assert.True(dump.Plan.Slices.Count >= 2, $"{dump.Plan.Slices.Count} slices");
        Assert.Equal(Rows, dump.Plan.TotalRows);
        Assert.All(dump.Slices, s => Assert.True(s.IsSuccess, s.Message));
        var described = dump.Plan.Tables.Single(t => t.Table == ArchiveCopyTable.Data).Columns.Select(c => c.Name).ToList();
        Assert.Contains("value", described);
        Assert.Contains(described, c => c.StartsWith("valuedoubled", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(described, c => c.StartsWith("pin", StringComparison.OrdinalIgnoreCase));
        AssertNoPin(staged);
        Assert.Contains("valuedoubled", staged, StringComparison.OrdinalIgnoreCase);

        // Restore into another tenant: the Mongo part brings the Failed definition, the table is provisioned without Pin.
        var targetStore = new MemoryArchiveStore();
        targetStore.Put(failed);
        var target = NewLane(CrateStagingCopyFixture.NewTenantId(), source.Archive, targetStore, cache);
        await target.Repository.EnsureArchiveCreatedAsync(failed);
        var columns = await ColumnsAsync(target);
        Assert.Contains("value", columns);
        Assert.DoesNotContain(columns, c => c.StartsWith("pin", StringComparison.OrdinalIgnoreCase));

        var load = await target.Repository.LoadArchiveFromStagingAsync(target.Archive, dump.Plan, location, null, Ct);

        Assert.True(load.IsSuccess, string.Join("; ", load.Slices.Select(s => s.Message)));
        Assert.True(load.RowCountVerified);
        var restored = (await target.Store.GetAsync(target.Archive))!;
        Assert.Equal(CkArchiveStatus.Failed, restored.Status);
        Assert.Equal(["Pin"], restored.HiddenColumns);
        var history = await target.Repository.ExecuteQueryAsync(target.Archive,
            StreamDataQueryOptions.Create().WithCkTypeId(SensorType).WithColumns(["Value"])
                .WithTimeRange(FirstRow, FirstRow.AddDays(3)));
        Assert.Equal(Rows, history.Rows.Count);
    }

    [Fact]
    public async Task ColumnHiddenBeforeTheRevalidation_IsLeftOutOfDumpAndExport_AndTheRedactionResumesTheRestoredArchive()
    {
        var pinHidden = false;
        var cache = SensorCache(() => pinHidden);
        var source = await NewSourceAsync(cache);

        // The model changed, the re-validation has not run yet: the archive is still Activated with Pin in its definition.
        pinHidden = true;
        Assert.Equal(CkArchiveStatus.Activated, (await source.Store.GetAsync(source.Archive))!.Status);

        var (dump, location, staged) = await DumpAsync(source);
        Assert.All(dump.Slices, s => Assert.True(s.IsSuccess, s.Message));
        AssertNoPin(staged);

        var exported = new List<IReadOnlyDictionary<string, object?>>();
        await foreach (var row in source.Repository.ExportRowsAsync(source.Archive, null, Ct))
        {
            exported.Add(row);
        }

        Assert.Equal(Rows, exported.Count);
        Assert.All(exported, r => Assert.False(r.ContainsKey("pin")));

        // Restore: the re-validation of the restored definition fails it, the table is provisioned without Pin.
        var targetStore = new MemoryArchiveStore();
        targetStore.Put((await source.Store.GetAsync(source.Archive))!);
        var target = NewLane(CrateStagingCopyFixture.NewTenantId(), source.Archive, targetStore, cache);
        Assert.False(await target.Lifecycle.RevalidateAccessAsync(target.Archive));
        var failed = (await target.Store.GetAsync(target.Archive))!;
        await target.Repository.EnsureArchiveCreatedAsync(failed);
        var load = await target.Repository.LoadArchiveFromStagingAsync(target.Archive, dump.Plan, location, null, Ct);
        Assert.True(load.RowCountVerified, string.Join("; ", load.Slices.Select(s => s.Message)));

        // The redaction drops nothing that exists, removes Pin from the definition and activates the archive again.
        var result = await target.Lifecycle.RedactHiddenColumnsAsync(target.Archive);

        Assert.Equal(CkArchiveStatus.Activated, result.Status);
        Assert.Equal(["Pin"], result.RemovedColumns);
        await fixture.SqlAsync($"REFRESH TABLE {target.Table}");
        Assert.Equal(Rows, await fixture.CountAsync($"SELECT count(*) FROM {target.Table}"));
        await target.Repository.InsertAsync(target.Archive, new StreamDataPoint
        {
            RtId = OctoObjectId.GenerateNewId(), CkTypeId = SensorType, Timestamp = FirstRow.AddDays(5),
            Attributes = new Dictionary<string, object?> { ["Value"] = 7.5 }
        });
        await fixture.SqlAsync($"REFRESH TABLE {target.Table}");
        Assert.Equal(Rows + 1, await fixture.CountAsync($"SELECT count(*) FROM {target.Table}"));
    }

    [Fact]
    public async Task DisabledArchiveWithAHiddenColumn_IsProvisionedAndImportedWithoutIt_AndKeepsItsStatus()
    {
        var cache = SensorCache(() => true);
        var tenant = CrateStagingCopyFixture.NewTenantId();
        var archive = OctoObjectId.GenerateNewId();
        var store = new MemoryArchiveStore();
        var disabled = new ArchiveSnapshot(archive, SensorType, CkArchiveStatus.Disabled, "meter",
            new List<CkArchiveColumnSpec> { new("Value", Indexed: false, Required: false), new("Pin", Indexed: false, Required: true) })
        {
            ConflictPrecedence = [new ArchiveConflictKey("Pin", ConflictKeyOrder.HigherWins)]
        };
        store.Put(disabled);
        var lane = NewLane(tenant, archive, store, cache);

        await lane.Repository.EnsureArchiveCreatedAsync(disabled);
        await lane.Repository.ImportRowsAsync(archive, Rows3(), ArchiveImportMode.InsertOnly, Ct);
        await fixture.SqlAsync($"REFRESH TABLE {lane.Table}");

        var columns = await ColumnsAsync(lane);
        Assert.Contains("value", columns);
        Assert.DoesNotContain("pin", columns);
        Assert.Equal(3, await fixture.CountAsync($"SELECT count(*) FROM {lane.Table}"));
        Assert.Equal(CkArchiveStatus.Disabled, (await store.GetAsync(archive))!.Status);

        static async IAsyncEnumerable<IReadOnlyDictionary<string, object?>> Rows3()
        {
            await Task.CompletedTask;
            for (var i = 0; i < 3; i++)
            {
                yield return new Dictionary<string, object?>
                {
                    ["rtid"] = OctoObjectId.GenerateNewId().ToString(), ["cktypeid"] = SensorType.ToString(),
                    ["timestamp"] = FirstRow.AddHours(i), ["value"] = i * 1.0, ["pin"] = 1234.0
                };
            }
        }
    }

    [Fact]
    public async Task AnObjectColumnWithMembers_IsCopiedByItsTopLevelName_WhileAHiddenColumnIsLeftOut()
    {
        // information_schema lists meta['a'] next to meta; the COPY projection may only name meta.
        var cache = SensorCache(() => true);
        var source = await NewSourceAsync(SensorCache(() => false));
        source = NewLane(source.Tenant, source.Archive, source.Store, cache);
        await fixture.SqlAsync($"ALTER TABLE {source.Table} ADD COLUMN \"meta\" OBJECT(STRICT) AS (\"a\" DOUBLE PRECISION)");
        await fixture.SqlAsync($"UPDATE {source.Table} SET \"meta\" = {{\"a\" = 2.5}}");
        await fixture.SqlAsync($"REFRESH TABLE {source.Table}");

        var (dump, _, staged) = await DumpAsync(source);

        Assert.All(dump.Slices, s => Assert.True(s.IsSuccess, s.Message));
        var described = dump.Plan.Tables.Single(t => t.Table == ArchiveCopyTable.Data).Columns.Select(c => c.Name).ToList();
        Assert.Contains("meta", described);
        Assert.Contains("meta['a']", described);
        AssertNoPin(staged);
        Assert.All(staged.Split('\n', StringSplitOptions.RemoveEmptyEntries),
            l => Assert.Contains("\"meta\":{\"a\":2.5}", l, StringComparison.Ordinal));
    }

    [Fact]
    public async Task ImportIntoATableThatStillHasTheHiddenRequiredColumn_KeepsWritingIt()
    {
        // A Disabled archive keeps its table, its required Pin (NOT NULL) became Hidden: the import must not leave Pin out.
        var cache = SensorCache(() => true);
        var tenant = CrateStagingCopyFixture.NewTenantId();
        var archive = OctoObjectId.GenerateNewId();
        var store = new MemoryArchiveStore();
        store.Put(new ArchiveSnapshot(archive, SensorType, CkArchiveStatus.Disabled, "meter",
            new List<CkArchiveColumnSpec> { new("Value", Indexed: false, Required: false), new("Pin", Indexed: false, Required: true) }));
        var lane = NewLane(tenant, archive, store, cache);
        var doubleType = new CrateColumnType.Primitive("DOUBLE PRECISION");
        await fixture.SqlAsync(ArchiveDdlGenerator.GenerateCreateTable(lane.Table,
            [new ArchiveColumnDdl("Value", doubleType, false, false), new ArchiveColumnDdl("Pin", doubleType, true, false)],
            numberOfShards: 2, numberOfReplicas: 0));
        Assert.Contains("NOT NULL", (await fixture.QueryAsync(
            $"SHOW CREATE TABLE {lane.Table}")).Single().Values.Single()!.ToString()!.Split('\n').Single(l => l.Contains("\"pin\"")));

        await lane.Repository.ImportRowsAsync(archive, ImportRows(3), ArchiveImportMode.InsertOnly, Ct);
        await fixture.SqlAsync($"REFRESH TABLE {lane.Table}");

        Assert.Equal(3, await fixture.CountAsync($"SELECT count(*) FROM {lane.Table} WHERE \"pin\" = 1234.0"));
    }

    [Fact]
    public async Task ImportIntoATableThatHasOneHiddenColumnButLacksAnother_KeepsTheOneItHas()
    {
        // Pin (NOT NULL) is in the table, Secret became Hidden before the restore and is not: Pin keeps its values.
        var cache = SensorCacheWithSecret();
        var tenant = CrateStagingCopyFixture.NewTenantId();
        var archive = OctoObjectId.GenerateNewId();
        var store = new MemoryArchiveStore();
        store.Put(new ArchiveSnapshot(archive, SensorType, CkArchiveStatus.Disabled, "meter",
            new List<CkArchiveColumnSpec>
            {
                new("Value", Indexed: false, Required: false), new("Pin", Indexed: false, Required: true),
                new("Secret", Indexed: false, Required: false)
            }));
        var lane = NewLane(tenant, archive, store, cache);
        var doubleType = new CrateColumnType.Primitive("DOUBLE PRECISION");
        await fixture.SqlAsync(ArchiveDdlGenerator.GenerateCreateTable(lane.Table,
            [new ArchiveColumnDdl("Value", doubleType, false, false), new ArchiveColumnDdl("Pin", doubleType, true, false)],
            numberOfShards: 2, numberOfReplicas: 0));

        await lane.Repository.ImportRowsAsync(archive, ImportRows(3, withSecret: true), ArchiveImportMode.InsertOnly, Ct);
        await fixture.SqlAsync($"REFRESH TABLE {lane.Table}");

        Assert.Equal(3, await fixture.CountAsync($"SELECT count(*) FROM {lane.Table} WHERE \"pin\" = 1234.0"));
        Assert.DoesNotContain("secret", await ColumnsAsync(lane));
    }

    /// <summary>Test/Sensor with Value (visible) and the Hidden attributes Pin and Secret.</summary>
    private static ICkCacheService SensorCacheWithSecret()
    {
        var typeId = new CkId<CkTypeId>("Test-1.0.0/Sensor-1");
        CkTypeAttributeGraph Attribute(string name, CkAttributeAccessDto access) => new(
            new CkId<CkAttributeId>($"Test-1.0.0/{name}-1"), name, null, AttributeValueTypesDto.Double, null, null, null,
            null, null, isOptional: true, description: null) { Access = access };
        CkTypeGraph Graph() => new(typeId, isAbstract: false, isFinal: false, isCollectionRoot: true, [], null, null, [], [],
            new[]
            {
                Attribute("Value", CkAttributeAccessDto.ReadWrite), Attribute("Pin", CkAttributeAccessDto.Hidden),
                Attribute("Secret", CkAttributeAccessDto.Hidden)
            }.ToDictionary(a => a.CkAttributeId, a => a),
            [], new CkGraphDirectedAssociations([]), "sensor", enableChangeStreamPreAndPostImages: false);

        var cache = A.Fake<ICkCacheService>();
        A.CallTo(() => cache.GetRtCkType(A<string>._, SensorType)).ReturnsLazily(Graph);
        CkTypeGraph? ignored;
        A.CallTo(() => cache.TryGetRtCkType(A<string>._, SensorType, out ignored))
            .ReturnsLazily(() => true).AssignsOutAndRefParameters(Graph());
        return cache;
    }

    private static async IAsyncEnumerable<IReadOnlyDictionary<string, object?>> ImportRows(int count, bool withSecret = false)
    {
        await Task.CompletedTask;
        for (var i = 0; i < count; i++)
        {
            yield return new Dictionary<string, object?>
            {
                ["rtid"] = OctoObjectId.GenerateNewId().ToString(), ["cktypeid"] = SensorType.ToString(),
                ["timestamp"] = FirstRow.AddHours(i), ["value"] = i * 1.0, ["pin"] = 1234.0,
                ["secret"] = withSecret ? 99.0 : null
            };
        }
    }

    [Fact]
    public async Task ColumnVisibleAgain_IsProvisionedAndDumpedAsBefore()
    {
        var pinHidden = false;
        var cache = SensorCache(() => pinHidden);
        var source = await NewSourceAsync(cache);

        var (dump, _, staged) = await DumpAsync(source);

        Assert.Contains(dump.Plan.Tables.Single(t => t.Table == ArchiveCopyTable.Data).Columns, c => c.Name == "pin");
        Assert.Contains("\"pin\"", staged, StringComparison.Ordinal);

        // A Failed-on-Hidden definition whose Pin is visible again (the model was changed back) keeps Pin in its table.
        var targetStore = new MemoryArchiveStore();
        var failed = (await source.Store.GetAsync(source.Archive))! with
        {
            Status = CkArchiveStatus.Failed, StatusReason = "'Pin' (reaches the Hidden attribute 'Pin')", HiddenColumns = ["Pin"]
        };
        targetStore.Put(failed);
        var target = NewLane(CrateStagingCopyFixture.NewTenantId(), source.Archive, targetStore, cache);
        await target.Repository.EnsureArchiveCreatedAsync(failed);
        Assert.Contains("pin", await ColumnsAsync(target));
    }
}
