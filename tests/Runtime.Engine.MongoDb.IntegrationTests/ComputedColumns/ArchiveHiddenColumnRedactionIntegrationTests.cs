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
using StreamDataException = Meshmakers.Octo.Runtime.Contracts.StreamData.StreamDataException;

namespace Meshmakers.Octo.Runtime.Engine.MongoDb.IntegrationTests.ComputedColumns;

/// <summary>
///     AB#6247 (N3) against a real CrateDB: a raw archive whose column <c>Pin</c> became Hidden after activation goes
///     Failed with its reason and Hidden column recorded; its history stays queryable without <c>Pin</c>, every write is
///     refused; <c>RedactHiddenColumnsAsync</c> drops the column (and only it) from table and definition, and the archive
///     ingests again.
/// </summary>
[Collection(CrateStagingCopyCollection.Name)]
public class ArchiveHiddenColumnRedactionIntegrationTests(CrateStagingCopyFixture fixture)
{
    private static readonly RtCkId<CkTypeId> SensorType = new("Test", new CkTypeId("Sensor"));
    private static readonly DateTime FirstRow = new(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);

    private static readonly IFormulaEngine Formula =
        new ServiceCollection().AddFormulaEngine().BuildServiceProvider().GetRequiredService<IFormulaEngine>();

    private sealed record Lane(
        string Table, OctoObjectId Archive, MemoryArchiveStore Store, CrateDbStreamDataRepository Repository,
        ArchiveLifecycleService Lifecycle, IArchiveAuditTrail Audit);

    /// <summary>
    ///     Test/Sensor with the Double attributes Value and Pin; Pin is Hidden when <paramref name="pinHidden" />, Value when
    ///     <paramref name="valueHidden" />.
    /// </summary>
    private static ICkCacheService SensorCache(Func<bool> pinHidden, Func<bool>? valueHidden = null)
    {
        var typeId = new CkId<CkTypeId>("Test-1.0.0/Sensor-1");
        CkTypeAttributeGraph Attribute(string name, CkAttributeAccessDto access) => new(
            new CkId<CkAttributeId>($"Test-1.0.0/{name}-1"), name, null, AttributeValueTypesDto.Double, null, null, null,
            null, null, isOptional: true, description: null) { Access = access };
        CkTypeGraph Graph() => new(typeId, isAbstract: false, isFinal: false, isCollectionRoot: true, [], null, null, [], [],
            new[]
            {
                Attribute("Value", valueHidden?.Invoke() == true ? CkAttributeAccessDto.Hidden : CkAttributeAccessDto.ReadWrite),
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

    private async Task<Lane> NewLaneAsync(Func<bool> pinHidden, Func<bool>? valueHidden = null)
    {
        var tenant = CrateStagingCopyFixture.NewTenantId();
        var archive = OctoObjectId.GenerateNewId();
        await fixture.CreateRawArchiveAsync(tenant, archive);
        var table = TenantSchema.QualifiedArchiveTable(tenant, archive.ToString());
        await fixture.SqlAsync($"ALTER TABLE {table} ADD COLUMN \"pin\" DOUBLE PRECISION");

        var store = new MemoryArchiveStore();
        store.Put(new ArchiveSnapshot(archive, SensorType, CkArchiveStatus.Activated, "meter",
            new List<CkArchiveColumnSpec> { new("Value", Indexed: false, Required: false), new("Pin", Indexed: false, Required: false) }));

        var cache = SensorCache(pinHidden, valueHidden);
        var configuration = Options.Create(new StreamDataConfiguration { ConnectionString = fixture.ConnectionString });
        var repository = new CrateDbStreamDataRepository(NullLogger<CrateDbStreamDataRepository>.Instance, cache,
            fixture.Client, A.Fake<IStreamDataDatabaseManagementClient>(), configuration, tenant, store, Formula);
        var audit = A.Fake<IArchiveAuditTrail>();
        var lifecycle = new ArchiveLifecycleService(tenant, store, repository, audit,
            NullLogger<ArchiveLifecycleService>.Instance, recomputeJobStore: new MemoryJobStore(), ckCacheService: cache);
        // 48 hourly rows written through the repository while Pin was still visible.
        await repository.InsertAsync(archive, Enumerable.Range(0, 48).Select(i => new StreamDataPoint
        {
            RtId = OctoObjectId.GenerateNewId(), CkTypeId = SensorType, Timestamp = FirstRow.AddHours(i),
            Attributes = new Dictionary<string, object?> { ["Value"] = i * 1.5, ["Pin"] = 1234.0 }
        }).ToList());
        await fixture.SqlAsync($"REFRESH TABLE {table}");
        return new Lane(table, archive, store, repository, lifecycle, audit);
    }

    private async Task<string?> ColumnTypeAsync(Lane lane, string column)
    {
        var schemaAndTable = lane.Table.Replace("\"", string.Empty).Split('.');
        var rows = await fixture.QueryAsync(
            "SELECT data_type FROM information_schema.columns " +
            $"WHERE table_schema = '{schemaAndTable[0]}' AND table_name = '{schemaAndTable[1]}' AND column_name = '{column}'");
        return rows.SingleOrDefault()?["data_type"]?.ToString();
    }

    private StreamDataPoint Point(DateTime at, double value) => new()
    {
        RtId = OctoObjectId.GenerateNewId(), CkTypeId = SensorType, Timestamp = at,
        Attributes = new Dictionary<string, object?> { ["Value"] = value }
    };

    [Fact]
    public async Task FailedOnHiddenColumns_KeepsItsHistoryReadable_AndTheRedactionResumesIt()
    {
        var pinHidden = false;
        var lane = await NewLaneAsync(() => pinHidden);

        // A model change makes Pin Hidden; the re-validation fails the archive and records why.
        pinHidden = true;
        Assert.False(await lane.Lifecycle.RevalidateAccessAsync(lane.Archive));
        var failed = (await lane.Store.GetAsync(lane.Archive))!;
        Assert.Equal(CkArchiveStatus.Failed, failed.Status);
        Assert.Equal(["Pin"], failed.HiddenColumns);
        Assert.Contains("'Pin' (reaches the Hidden attribute 'Pin')", failed.StatusReason);

        // History: readable without Pin, Pin refused by name, writes refused.
        var history = await lane.Repository.ExecuteQueryAsync(lane.Archive,
            StreamDataQueryOptions.Create().WithCkTypeId(SensorType).WithColumns(["Value"])
                .WithTimeRange(FirstRow, FirstRow.AddDays(3)));
        Assert.Equal(48, history.Rows.Count);
        Assert.All(history.Rows, r => Assert.DoesNotContain(r.Values.Keys, k => k.Contains("pin", StringComparison.OrdinalIgnoreCase)));
        await Assert.ThrowsAnyAsync<StreamDataException>(() => lane.Repository.ExecuteQueryAsync(lane.Archive,
            StreamDataQueryOptions.Create().WithCkTypeId(SensorType).WithColumns(["Pin"])));
        await Assert.ThrowsAsync<ArchiveNotActivatedException>(() =>
            lane.Repository.InsertAsync(lane.Archive, Point(FirstRow.AddDays(5), 1.0)));

        // The row export leaves Pin out as well.
        var exported = new List<IReadOnlyDictionary<string, object?>>();
        await foreach (var row in lane.Repository.ExportRowsAsync(lane.Archive, null, TestContext.Current.CancellationToken))
        {
            exported.Add(row);
        }

        Assert.Equal(48, exported.Count);
        Assert.All(exported, r => Assert.False(r.ContainsKey("pin")));
        Assert.All(exported, r => Assert.True(r.ContainsKey("value")));

        // Redaction (the archive is Failed): Pin is gone from table and definition, Value and the rows stay, the archive ingests again.
        var result = await lane.Lifecycle.RedactHiddenColumnsAsync(lane.Archive);

        Assert.Equal(CkArchiveStatus.Activated, result.Status);
        Assert.Equal(["Pin"], result.RemovedColumns);
        Assert.Null(await ColumnTypeAsync(lane, "pin"));
        Assert.NotNull(await ColumnTypeAsync(lane, "value"));
        var redacted = (await lane.Store.GetAsync(lane.Archive))!;
        Assert.Equal(["Value"], redacted.Columns.Select(c => c.Path));
        Assert.Empty(redacted.HiddenColumns);
        Assert.Null(redacted.StatusReason);
        A.CallTo(() => lane.Audit.RecordTransitionAsync(A<string>._, lane.Archive, CkArchiveStatus.Failed,
            CkArchiveStatus.Failed, A<string>.That.Contains("RedactHiddenColumns drops"))).MustHaveHappenedOnceExactly();

        await fixture.SqlAsync($"REFRESH TABLE {lane.Table}");
        Assert.Equal(48, await fixture.CountAsync($"SELECT count(*) FROM {lane.Table}"));
        await lane.Repository.InsertAsync(lane.Archive, Point(FirstRow.AddDays(5), 7.5));
        await fixture.SqlAsync($"REFRESH TABLE {lane.Table}");
        Assert.Equal(49, await fixture.CountAsync($"SELECT count(*) FROM {lane.Table}"));

        // Idempotent: a second call drops nothing.
        var again = await lane.Lifecycle.RedactHiddenColumnsAsync(lane.Archive);
        Assert.Empty(again.RemovedColumns);
        Assert.Equal(CkArchiveStatus.Activated, again.Status);
    }

    /// <summary>
    ///     The confirmed redaction: the administrator confirmed Pin; a later model change makes Value Hidden as well. The call
    ///     is refused with both sets, and table, rows, values, definition, status and audit stay untouched. Once the set is
    ///     Pin again, the confirmed call drops Pin and only Pin.
    /// </summary>
    [Fact]
    public async Task ConfirmedRedaction_DropsOnlyTheConfirmedColumns_AndRefusesAChangedSetWithoutAnyDataLoss()
    {
        var pinHidden = false;
        var valueHidden = false;
        var lane = await NewLaneAsync(() => pinHidden, () => valueHidden);

        pinHidden = true;
        Assert.False(await lane.Lifecycle.RevalidateAccessAsync(lane.Archive));
        Assert.Equal(["Pin"], (await lane.Store.GetAsync(lane.Archive))!.HiddenColumns);

        // Between the administrator's read and the call, Value becomes Hidden too.
        valueHidden = true;
        var refused = await Assert.ThrowsAsync<HiddenColumnRedactionColumnsChangedException>(() =>
            lane.Lifecycle.RedactConfirmedHiddenColumnsAsync(lane.Archive, ["Pin"]));
        Assert.Equal(["Pin"], refused.ExpectedColumns);
        Assert.Equal(["Value", "Pin"], refused.CurrentColumns);

        // An unknown column is refused the same way.
        await Assert.ThrowsAsync<HiddenColumnRedactionColumnsChangedException>(() =>
            lane.Lifecycle.RedactConfirmedHiddenColumnsAsync(lane.Archive, ["Pin", "Value", "NoSuchColumn"]));

        await AssertNothingWasDroppedAsync(lane);

        // The set is Pin again: the confirmed call (case-insensitive) drops Pin, keeps Value with every value, and resumes.
        valueHidden = false;
        var result = await lane.Lifecycle.RedactConfirmedHiddenColumnsAsync(lane.Archive, ["pin"]);

        Assert.Equal(CkArchiveStatus.Activated, result.Status);
        Assert.Equal(["Pin"], result.RemovedColumns);
        Assert.Null(await ColumnTypeAsync(lane, "pin"));
        Assert.NotNull(await ColumnTypeAsync(lane, "value"));
        Assert.Equal(["Value"], (await lane.Store.GetAsync(lane.Archive))!.Columns.Select(c => c.Path));
        await AssertOriginalValuesAsync(lane);
        A.CallTo(() => lane.Audit.RecordTransitionAsync(A<string>._, lane.Archive, CkArchiveStatus.Failed,
            CkArchiveStatus.Failed, A<string>.That.Contains("RedactHiddenColumns drops"))).MustHaveHappenedOnceExactly();

        // A repeat of the same confirmation finds no Hidden column any more and is refused; nothing else changes.
        var repeated = await Assert.ThrowsAsync<HiddenColumnRedactionColumnsChangedException>(() =>
            lane.Lifecycle.RedactConfirmedHiddenColumnsAsync(lane.Archive, ["Pin"]));
        Assert.Empty(repeated.CurrentColumns);
        Assert.Equal(CkArchiveStatus.Activated, (await lane.Store.GetAsync(lane.Archive))!.Status);
    }

    /// <summary>Every row still carries the Value it was written with: hour i of the setup holds i * 1.5.</summary>
    private async Task AssertOriginalValuesAsync(Lane lane)
    {
        await fixture.SqlAsync($"REFRESH TABLE {lane.Table}");
        var rows = await fixture.QueryAsync($"SELECT \"timestamp\", \"value\" FROM {lane.Table} ORDER BY \"timestamp\"");
        Assert.Equal(48, rows.Count);
        for (var i = 0; i < rows.Count; i++)
        {
            // Convert.ToDouble(null) is 0, the value of hour 0: a lost value must fail as NULL, not pass as 0.
            var value = rows[i]["value"];
            Assert.NotNull(value);
            Assert.Equal(i * 1.5, Convert.ToDouble(value, System.Globalization.CultureInfo.InvariantCulture));
        }
    }

    /// <summary>
    ///     Both columns, every row and every stored value, the definition, the Failed status; no redaction audit entry (the
    ///     re-validation's Failed transition is audited before).
    /// </summary>
    private async Task AssertNothingWasDroppedAsync(Lane lane)
    {
        Assert.NotNull(await ColumnTypeAsync(lane, "pin"));
        Assert.NotNull(await ColumnTypeAsync(lane, "value"));
        await fixture.SqlAsync($"REFRESH TABLE {lane.Table}");
        Assert.Equal(48, await fixture.CountAsync($"SELECT count(*) FROM {lane.Table}"));
        Assert.Equal(48, await fixture.CountAsync($"SELECT count(*) FROM {lane.Table} WHERE \"pin\" = 1234.0"));
        await AssertOriginalValuesAsync(lane);
        var snapshot = (await lane.Store.GetAsync(lane.Archive))!;
        Assert.Equal(["Value", "Pin"], snapshot.Columns.Select(c => c.Path));
        Assert.Equal(CkArchiveStatus.Failed, snapshot.Status);
        Assert.Equal(["Pin"], snapshot.HiddenColumns);
        A.CallTo(() => lane.Audit.RecordTransitionAsync(A<string>._, A<OctoObjectId>._, A<CkArchiveStatus>._,
            A<CkArchiveStatus>._, A<string>.That.Contains("RedactHiddenColumns"))).MustNotHaveHappened();
    }

    [Fact]
    public async Task AComputedColumnOverAHiddenColumn_IsNeitherExportedNorQueryable_AndBlocksTheRedaction()
    {
        var pinHidden = false;
        var lane = await NewLaneAsync(() => pinHidden);
        await lane.Lifecycle.AddComputedColumnAsync(lane.Archive, "pinDoubled", "pin * 2", FormulaResultType.Double,
            indexed: false);

        pinHidden = true;
        Assert.False(await lane.Lifecycle.RevalidateAccessAsync(lane.Archive));

        var exported = new List<IReadOnlyDictionary<string, object?>>();
        await foreach (var row in lane.Repository.ExportRowsAsync(lane.Archive, null, TestContext.Current.CancellationToken))
        {
            exported.Add(row);
        }

        Assert.Equal(48, exported.Count);
        Assert.All(exported, r => Assert.DoesNotContain(r.Keys, k => k.StartsWith("pin", StringComparison.OrdinalIgnoreCase)));
        await Assert.ThrowsAnyAsync<StreamDataException>(() => lane.Repository.ExecuteQueryAsync(lane.Archive,
            StreamDataQueryOptions.Create().WithCkTypeId(SensorType).WithColumns(["pinDoubled"])));
        await Assert.ThrowsAnyAsync<StreamDataException>(() => lane.Lifecycle.RedactHiddenColumnsAsync(lane.Archive));
        Assert.NotNull(await ColumnTypeAsync(lane, "pin"));
    }

    [Fact]
    public async Task DropIngestedColumnStorage_IsIdempotent_AndNeverDropsAStandardColumn()
    {
        var lane = await NewLaneAsync(() => false);
        var snapshot = (await lane.Store.GetAsync(lane.Archive))!;

        await lane.Repository.DropIngestedColumnStorageAsync(snapshot, ["Pin"], TestContext.Current.CancellationToken);
        await lane.Repository.DropIngestedColumnStorageAsync(snapshot, ["Pin"], TestContext.Current.CancellationToken);
        Assert.Null(await ColumnTypeAsync(lane, "pin"));

        await Assert.ThrowsAsync<ArchivePathInvalidException>(() =>
            lane.Repository.DropIngestedColumnStorageAsync(snapshot, ["Timestamp"], TestContext.Current.CancellationToken));
        Assert.NotNull(await ColumnTypeAsync(lane, "timestamp"));

        // "P.in" maps to the physical column "pin" too: a kept column must never lose its storage.
        var sharing = snapshot with
        {
            Columns = [new CkArchiveColumnSpec("Value", false, false), new CkArchiveColumnSpec("P.in", false, false)]
        };
        await Assert.ThrowsAsync<ArchivePathInvalidException>(() =>
            lane.Repository.DropIngestedColumnStorageAsync(sharing with
            {
                Columns = [.. sharing.Columns, new CkArchiveColumnSpec("Pin", false, false)]
            }, ["Pin"], TestContext.Current.CancellationToken));
    }
}
