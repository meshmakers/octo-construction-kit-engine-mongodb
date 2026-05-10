using FakeItEasy;
using Meshmakers.Octo.ConstructionKit.Contracts;
using Meshmakers.Octo.ConstructionKit.Contracts.Services;
using Meshmakers.Octo.Runtime.Contracts.StreamData;
using Meshmakers.Octo.Runtime.Engine.CrateDb;
using Meshmakers.Octo.Runtime.Engine.CrateDb.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Meshmakers.Octo.Runtime.Engine.UnitTests;

/// <summary>
/// Verifies the per-archive status guard added in T7. Each data-plane method must reject calls on
/// archives that don't exist or aren't in <see cref="CkArchiveStatus.Activated"/>; the guard is
/// skipped only when the repository is constructed without an <see cref="ICkArchiveRuntimeStore"/>
/// (transitional T7 wiring state).
/// </summary>
public class CrateDbStreamDataRepositoryStatusCheckTests
{
    private static readonly OctoObjectId Archive = OctoObjectId.GenerateNewId();
    private static readonly RtCkId<CkTypeId> SomeType = new("Test", new CkTypeId("TempSensor"));

    private readonly IStreamDataDatabaseClient _db = A.Fake<IStreamDataDatabaseClient>();
    private readonly IStreamDataDatabaseManagementClient _mgmt = A.Fake<IStreamDataDatabaseManagementClient>();
    private readonly ICkCacheService _cache = A.Fake<ICkCacheService>();
    private readonly ICkArchiveRuntimeStore _store = A.Fake<ICkArchiveRuntimeStore>();

    private static readonly IOptions<StreamDataConfiguration> Config =
        Options.Create(new StreamDataConfiguration { ConnectionString = "Host=ignored" });

    private CrateDbStreamDataRepository NewSut(bool withStore = true) =>
        new(NullLogger<CrateDbStreamDataRepository>.Instance,
            _cache, _db, _mgmt, Config, "tenant-x",
            withStore ? _store : null);

    private void Stub(CkArchiveStatus status) =>
        A.CallTo(() => _store.GetAsync(Archive))
            .Returns(new CkArchiveSnapshot(Archive, SomeType, status, null, Array.Empty<CkArchiveColumnSpec>()));

    [Theory]
    [InlineData(CkArchiveStatus.Created)]
    [InlineData(CkArchiveStatus.Disabled)]
    [InlineData(CkArchiveStatus.Failed)]
    public async Task Insert_NotActivated_ThrowsArchiveNotActivatedException(CkArchiveStatus status)
    {
        Stub(status);
        var ex = await Assert.ThrowsAsync<ArchiveNotActivatedException>(
            () => NewSut().InsertAsync(Archive, NewPoint()));
        Assert.Equal(status, ex.ActualStatus);
        // No DB call leaked through.
        A.CallTo(() => _db.InsertDataAsync(A<string>._, A<string>._, A<IReadOnlyList<string>>._, A<Meshmakers.Octo.Runtime.Engine.CrateDb.Dtos.DataPointDto>._)).MustNotHaveHappened();
    }

    [Fact]
    public async Task Insert_NoSnapshot_ThrowsArchiveNotFoundException()
    {
        A.CallTo(() => _store.GetAsync(Archive))
            .Returns(Task.FromResult<CkArchiveSnapshot?>(null));

        await Assert.ThrowsAsync<ArchiveNotFoundException>(
            () => NewSut().InsertAsync(Archive, NewPoint()));
    }

    [Fact]
    public async Task Insert_Activated_PassesStatusCheckAndInvokesDb()
    {
        Stub(CkArchiveStatus.Activated);

        await NewSut().InsertAsync(Archive, NewPoint());

        A.CallTo(() => _db.InsertDataAsync(
                "tenant-x",
                A<string>.That.Matches(t => t.Contains($"archive_{Archive}")),
                A<IReadOnlyList<string>>._,
                A<Meshmakers.Octo.Runtime.Engine.CrateDb.Dtos.DataPointDto>._))
            .MustHaveHappenedOnceExactly();
    }

    [Fact]
    public async Task Insert_NoStoreWired_SkipsCheckAndStillCallsDb()
    {
        await NewSut(withStore: false).InsertAsync(Archive, NewPoint());

        A.CallTo(() => _store.GetAsync(A<OctoObjectId>._)).MustNotHaveHappened();
        A.CallTo(() => _db.InsertDataAsync(
                "tenant-x", A<string>._, A<IReadOnlyList<string>>._,
                A<Meshmakers.Octo.Runtime.Engine.CrateDb.Dtos.DataPointDto>._))
            .MustHaveHappenedOnceExactly();
    }

    [Fact]
    public async Task BulkInsert_NotActivated_ThrowsBeforeAnyDbCall()
    {
        Stub(CkArchiveStatus.Disabled);

        await Assert.ThrowsAsync<ArchiveNotActivatedException>(
            () => NewSut().InsertAsync(Archive, new[] { NewPoint() }));
        A.CallTo(() => _db.InsertDataAsync(
                A<string>._, A<string>._, A<IReadOnlyList<string>>._,
                A<IEnumerable<Meshmakers.Octo.Runtime.Engine.CrateDb.Dtos.DataPointDto>>._))
            .MustNotHaveHappened();
    }

    [Fact]
    public async Task EnsureArchiveCreated_EmitsCreateTableDdlForPerArchiveTable()
    {
        // Empty columns list: the DDL should still emit the standard time-series columns and
        // generate a `CREATE TABLE IF NOT EXISTS "tenantx"."archive_<rtId>"` against the per-tenant
        // schema rather than the legacy single-table-per-tenant path.
        var snapshot = new CkArchiveSnapshot(Archive, SomeType, CkArchiveStatus.Created, null, Array.Empty<CkArchiveColumnSpec>());

        await NewSut().EnsureArchiveCreatedAsync(snapshot);

        A.CallTo(() => _mgmt.ExecuteDdlAsync(
                "tenant-x",
                A<string>.That.Matches(sql =>
                    sql.Contains("CREATE TABLE IF NOT EXISTS")
                    && sql.Contains($"archive_{Archive}"))))
            .MustHaveHappenedOnceExactly();
    }

    [Fact]
    public async Task DeleteArchive_EmitsDropTableDdlForPerArchiveTable()
    {
        await NewSut().DeleteArchiveAsync(Archive);

        A.CallTo(() => _mgmt.ExecuteDdlAsync(
                "tenant-x",
                A<string>.That.Matches(sql =>
                    sql.Contains("DROP TABLE IF EXISTS")
                    && sql.Contains($"archive_{Archive}"))))
            .MustHaveHappenedOnceExactly();
    }

    private static StreamDataPoint NewPoint() => new()
    {
        RtId = OctoObjectId.GenerateNewId(),
        CkTypeId = SomeType,
        Timestamp = DateTime.UtcNow,
    };
}
