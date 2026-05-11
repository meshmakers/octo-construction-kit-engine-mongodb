using System;
using FakeItEasy;
using Meshmakers.Octo.ConstructionKit.Contracts;
using Meshmakers.Octo.ConstructionKit.Contracts.Services;
using Meshmakers.Octo.Runtime.Contracts.StreamData;
using Meshmakers.Octo.Runtime.Engine.CrateDb;
using Microsoft.Extensions.Logging.Abstractions;

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

    private CrateDbStreamDataRepository NewSut(bool withStore = true) =>
        new(NullLogger<CrateDbStreamDataRepository>.Instance,
            _cache, _db, _mgmt, "tenant-x",
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
        A.CallTo(() => _db.InsertDataAsync(A<string>._, A<Meshmakers.Octo.Runtime.Engine.CrateDb.Dtos.DataPointDto>._)).MustNotHaveHappened();
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

        A.CallTo(() => _db.InsertDataAsync("tenant-x", A<Meshmakers.Octo.Runtime.Engine.CrateDb.Dtos.DataPointDto>._)).MustHaveHappenedOnceExactly();
    }

    [Fact]
    public async Task Insert_NoStoreWired_SkipsCheckAndStillCallsDb()
    {
        await NewSut(withStore: false).InsertAsync(Archive, NewPoint());

        A.CallTo(() => _store.GetAsync(A<OctoObjectId>._)).MustNotHaveHappened();
        A.CallTo(() => _db.InsertDataAsync("tenant-x", A<Meshmakers.Octo.Runtime.Engine.CrateDb.Dtos.DataPointDto>._)).MustHaveHappenedOnceExactly();
    }

    [Fact]
    public async Task BulkInsert_NotActivated_ThrowsBeforeAnyDbCall()
    {
        Stub(CkArchiveStatus.Disabled);

        await Assert.ThrowsAsync<ArchiveNotActivatedException>(
            () => NewSut().InsertAsync(Archive, new[] { NewPoint() }));
        A.CallTo(() => _db.InsertDataAsync(A<string>._, A<IEnumerable<Meshmakers.Octo.Runtime.Engine.CrateDb.Dtos.DataPointDto>>._))
            .MustNotHaveHappened();
    }

    [Fact]
    public async Task EnsureArchiveCreated_DelegatesToManagementClient()
    {
        var snapshot = new CkArchiveSnapshot(
            Archive, SomeType, CkArchiveStatus.Created, null, Array.Empty<CkArchiveColumnSpec>());
        await NewSut().EnsureArchiveCreatedAsync(snapshot);

        A.CallTo(() => _mgmt.CreateStreamDataTableIfNotExistAsync("tenant-x"))
            .MustHaveHappenedOnceExactly();
    }

    [Fact]
    public async Task DeleteArchive_DelegatesToManagementClient()
    {
        await NewSut().DeleteArchiveAsync(Archive);

        A.CallTo(() => _mgmt.DeleteStreamDataDatabaseAsync("tenant-x"))
            .MustHaveHappenedOnceExactly();
    }

    private static StreamDataPoint NewPoint() => new()
    {
        RtId = OctoObjectId.GenerateNewId(),
        CkTypeId = SomeType,
        Timestamp = DateTime.UtcNow,
    };
}
