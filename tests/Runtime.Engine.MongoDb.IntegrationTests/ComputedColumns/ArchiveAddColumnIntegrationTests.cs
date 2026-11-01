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
///     AB#7997 against a real CrateDB: an ingested column added to an activated raw archive becomes a nullable column of
///     the table, the existing rows keep their data and read NULL for it, the next ingested row fills it, and a computed
///     column can reference it. An unknown path changes neither the table nor the definition.
/// </summary>
[Collection(CrateStagingCopyCollection.Name)]
public class ArchiveAddColumnIntegrationTests(CrateStagingCopyFixture fixture)
{
    private static readonly RtCkId<CkTypeId> SensorType = new("Test", new CkTypeId("Sensor"));
    private static readonly DateTime FirstRow = new(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);

    private static readonly IFormulaEngine Formula =
        new ServiceCollection().AddFormulaEngine().BuildServiceProvider().GetRequiredService<IFormulaEngine>();

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private sealed record Lane(
        string Tenant, OctoObjectId Archive, string Table, MemoryArchiveStore Inner, CrateDbStreamDataRepository Repository,
        ArchiveLifecycleService Lifecycle);

    /// <summary>Test/Sensor with the Double attributes Value and DischargingPower.</summary>
    private static ICkCacheService SensorCache()
    {
        var typeId = new CkId<CkTypeId>("Test-1.0.0/Sensor-1");
        CkTypeAttributeGraph Attribute(string name) => new(new CkId<CkAttributeId>($"Test-1.0.0/{name}-1"), name, null,
            AttributeValueTypesDto.Double, null, null, null, null, null, isOptional: true, description: null);
        var attributes = new[] { Attribute("Value"), Attribute("DischargingPower") }
            .ToDictionary(a => a.CkAttributeId, a => a);
        var graph = new CkTypeGraph(typeId, isAbstract: false, isFinal: false, isCollectionRoot: true, [], null, null, [], [],
            attributes, [], new CkGraphDirectedAssociations([]), "sensor", enableChangeStreamPreAndPostImages: false);

        var cache = A.Fake<ICkCacheService>();
        A.CallTo(() => cache.GetRtCkType(A<string>._, SensorType)).Returns(graph);
        CkTypeGraph? ignored;
        A.CallTo(() => cache.TryGetRtCkType(A<string>._, SensorType, out ignored))
            .Returns(true).AssignsOutAndRefParameters(graph);
        return cache;
    }

    private async Task<Lane> NewLaneAsync(int hourlyRows)
    {
        var tenant = CrateStagingCopyFixture.NewTenantId();
        var archive = OctoObjectId.GenerateNewId();
        await fixture.CreateRawArchiveAsync(tenant, archive, hourlyRows);

        var inner = new MemoryArchiveStore();
        inner.Put(new ArchiveSnapshot(archive, SensorType, CkArchiveStatus.Activated, "battery",
            new List<CkArchiveColumnSpec> { new("Value", Indexed: false, Required: false) }));
        // The memory store of the backfill tests replaces columns by name; an ingested column has none.
        var store = A.Fake<IArchiveRuntimeStore>(o => o.Wrapping(inner));
        A.CallTo(() => store.AddIngestedColumnAsync(archive, A<CkArchiveColumnSpec>._))
            .ReturnsLazily(async (OctoObjectId id, CkArchiveColumnSpec column) =>
            {
                var current = (await inner.GetAsync(id))!;
                inner.Put(current with { Columns = current.Columns.Append(column).ToList() });
            });

        var configuration = Options.Create(new StreamDataConfiguration { ConnectionString = fixture.ConnectionString });
        var repository = new CrateDbStreamDataRepository(NullLogger<CrateDbStreamDataRepository>.Instance, SensorCache(),
            fixture.Client, A.Fake<IStreamDataDatabaseManagementClient>(), configuration, tenant, store, Formula);
        var lifecycle = new ArchiveLifecycleService(tenant, store, repository, A.Fake<IArchiveAuditTrail>(),
            NullLogger<ArchiveLifecycleService>.Instance, recomputeJobStore: new MemoryJobStore());
        return new Lane(tenant, archive, TenantSchema.QualifiedArchiveTable(tenant, archive.ToString()), inner, repository,
            lifecycle);
    }

    private async Task<long> CountAsync(Lane lane, string where = "true")
    {
        await fixture.SqlAsync($"REFRESH TABLE {lane.Table}");
        return await fixture.CountAsync($"SELECT count(*) FROM {lane.Table} WHERE {where}");
    }

    private async Task<string?> ColumnTypeAsync(Lane lane, string column)
    {
        var schemaAndTable = lane.Table.Replace("\"", string.Empty).Split('.');
        var rows = await fixture.QueryAsync(
            "SELECT data_type FROM information_schema.columns " +
            $"WHERE table_schema = '{schemaAndTable[0]}' AND table_name = '{schemaAndTable[1]}' AND column_name = '{column}'");
        return rows.SingleOrDefault()?["data_type"]?.ToString();
    }

    [Fact]
    public async Task AddedColumn_KeepsTheHistory_ExistingRowsReadNull_AndTheNextRowFillsIt()
    {
        var lane = await NewLaneAsync(hourlyRows: 300);
        var valueSum = await fixture.CountAsync($"SELECT CAST(sum(\"value\") * 10 AS BIGINT) FROM {lane.Table}");

        var added = await lane.Lifecycle.AddArchiveColumnAsync(lane.Archive, "DischargingPower", indexed: false, Ct);

        Assert.True(added);
        Assert.NotNull(await ColumnTypeAsync(lane, "dischargingpower"));
        Assert.Equal(300, await CountAsync(lane));
        Assert.Equal(300, await CountAsync(lane, "\"dischargingpower\" IS NULL"));
        Assert.Equal(valueSum, await fixture.CountAsync($"SELECT CAST(sum(\"value\") * 10 AS BIGINT) FROM {lane.Table}"));
        Assert.Contains((await lane.Inner.GetAsync(lane.Archive))!.Columns, c => c.Path == "DischargingPower");

        await lane.Repository.InsertAsync(lane.Archive, new StreamDataPoint
        {
            RtId = OctoObjectId.GenerateNewId(),
            CkTypeId = SensorType,
            Timestamp = FirstRow.AddDays(30),
            Attributes = new Dictionary<string, object?> { ["Value"] = 4.0, ["DischargingPower"] = 2.5 },
        });

        Assert.Equal(301, await CountAsync(lane));
        Assert.Equal(1, await CountAsync(lane, "\"dischargingpower\" = 2.5"));
    }

    [Fact]
    public async Task AddedColumn_CanBeReferencedByAComputedColumn()
    {
        var lane = await NewLaneAsync(hourlyRows: 48);
        await lane.Lifecycle.AddArchiveColumnAsync(lane.Archive, "DischargingPower", indexed: false, Ct);
        await lane.Repository.InsertAsync(lane.Archive, new StreamDataPoint
        {
            RtId = OctoObjectId.GenerateNewId(),
            CkTypeId = SensorType,
            Timestamp = FirstRow.AddDays(10),
            Attributes = new Dictionary<string, object?> { ["Value"] = 1.0, ["DischargingPower"] = 3.0 },
        });
        // The backfill reads the bounds of the table through a search; make the new row visible to it.
        await fixture.SqlAsync($"REFRESH TABLE {lane.Table}");

        await lane.Lifecycle.EnqueueAddComputedColumnAsync(lane.Archive, "dischargeKw", "DischargingPower / 1000",
            FormulaResultType.Double, indexed: false, Ct);
        Assert.Equal(1, await lane.Lifecycle.RunPendingComputedColumnBackfillsAsync(Ct));

        Assert.Equal(ComputedColumnState.Active, lane.Inner.Column(lane.Archive, "dischargeKw")!.ComputedState);
        Assert.Equal(1, await CountAsync(lane, "abs(\"dischargekw\" - 0.003) < 0.0000001"));
        Assert.Equal(48, await CountAsync(lane, "\"dischargekw\" IS NULL"));
    }

    [Fact]
    public async Task AnUnknownPath_ChangesNeitherTheTableNorTheDefinition()
    {
        var lane = await NewLaneAsync(hourlyRows: 10);

        await Assert.ThrowsAsync<ArchivePathInvalidException>(
            () => lane.Lifecycle.AddArchiveColumnAsync(lane.Archive, "NoSuchAttribute", indexed: false, Ct));

        Assert.Null(await ColumnTypeAsync(lane, "nosuchattribute"));
        Assert.Single((await lane.Inner.GetAsync(lane.Archive))!.Columns);
    }

    [Fact]
    public async Task AddingTheSameColumnTwice_IsIdempotent()
    {
        var lane = await NewLaneAsync(hourlyRows: 10);

        Assert.True(await lane.Lifecycle.AddArchiveColumnAsync(lane.Archive, "DischargingPower", indexed: false, Ct));
        Assert.False(await lane.Lifecycle.AddArchiveColumnAsync(lane.Archive, "DischargingPower", indexed: false, Ct));
        // The storage call alone (a retry after the definition write failed) also passes on the existing column.
        await lane.Repository.AddIngestedColumnStorageAsync((await lane.Inner.GetAsync(lane.Archive))!, "DischargingPower", Ct);

        Assert.Equal(2, (await lane.Inner.GetAsync(lane.Archive))!.Columns.Count);
    }
}
