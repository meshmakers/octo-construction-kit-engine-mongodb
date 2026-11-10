using FakeItEasy;
using Meshmakers.Octo.ConstructionKit.Contracts;
using Meshmakers.Octo.ConstructionKit.Contracts.Services;
using Meshmakers.Octo.Runtime.Contracts.Formulas;
using Meshmakers.Octo.Runtime.Contracts.StreamData;
using Meshmakers.Octo.Runtime.Engine.CrateDb;
using Meshmakers.Octo.Runtime.Engine.MongoDb.IntegrationTests.StagingCopy;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Meshmakers.Octo.Runtime.Engine.MongoDb.IntegrationTests.StreamDataExportImport;

/// <summary>
///     AB#8735 against a real CrateDB: the archive list read its record count from <c>sys.shards.num_docs</c>, which
///     only counts the rows of the last refresh. CrateDB does not refresh a shard nobody searched for 30 s ("search
///     idle"), so an archive that was imported into (or written to) while nobody read it showed 0 records. The count is
///     read after a refresh of the table; a search alone only wakes the shard, the rows appear at its next periodic
///     refresh.
/// </summary>
[Collection(CrateStagingCopyCollection.Name)]
public class ArchiveStatsAfterImportIntegrationTests(CrateStagingCopyFixture fixture)
{
    private static readonly RtCkId<CkTypeId> Type = new("Test", new CkTypeId("Meter"));

    /// <summary>CrateDB's default <c>index.search.idle.after</c> is 30 s; the table must be idle when the rows arrive.</summary>
    private static readonly TimeSpan SearchIdle = TimeSpan.FromSeconds(35);

    [Fact]
    public async Task TheRecordCount_IncludesRowsImportedIntoASearchIdleArchive()
    {
        var ct = TestContext.Current.CancellationToken;
        var tenant = CrateStagingCopyFixture.NewTenantId();
        var rtId = OctoObjectId.GenerateNewId();
        var snapshot = new ArchiveSnapshot(rtId, Type, CkArchiveStatus.Disabled, "measurements",
            [new CkArchiveColumnSpec("Value", false, false)]);
        var store = A.Fake<IArchiveRuntimeStore>();
        A.CallTo(() => store.GetAsync(rtId)).Returns(snapshot);
        var repository = new CrateDbStreamDataRepository(NullLogger<CrateDbStreamDataRepository>.Instance,
            A.Fake<ICkCacheService>(), fixture.Client, fixture.Client, fixture.Configuration, tenant, store,
            A.Fake<IFormulaEngine>());
        await fixture.SqlAsync(ArchiveDdlGenerator.GenerateCreateTable(TenantSchema.QualifiedArchiveTable(tenant, rtId.ToString()),
            [new ArchiveColumnDdl("Value", new CrateColumnType.Primitive("DOUBLE PRECISION"), false, false)], 1, 0));

        await Task.Delay(SearchIdle, ct);

        var rows = Enumerable.Range(0, 4).Select(i => (IReadOnlyDictionary<string, object?>)new Dictionary<string, object?>
        {
            [CrateDb.Constants.RtId] = OctoObjectId.GenerateNewId().ToString(),
            [CrateDb.Constants.CkTypeId] = Type.ToString(),
            [CrateDb.Constants.Timestamp] = new DateTime(2026, 1, 1, 0, i, 0, DateTimeKind.Utc),
            ["value"] = (double)i
        }).ToList();
        await repository.ImportRowsAsync(rtId, rows.ToAsyncEnumerable(), ArchiveImportMode.InsertOnly, ct);

        // No REFRESH TABLE and no search in between: the list reads the stats right after the import.
        var stats = await repository.GetArchiveStatsAsync([rtId], ct);

        Assert.True(stats[rtId].TableExists);
        Assert.Equal(4, stats[rtId].RecordCount);
    }
}
