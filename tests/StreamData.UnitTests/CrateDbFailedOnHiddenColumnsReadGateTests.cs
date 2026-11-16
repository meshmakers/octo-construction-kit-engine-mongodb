using FakeItEasy;
using Meshmakers.Octo.ConstructionKit.Contracts;
using Meshmakers.Octo.ConstructionKit.Contracts.Services;
using Meshmakers.Octo.Runtime.Contracts.Formulas;
using Meshmakers.Octo.Runtime.Contracts.StreamData;
using Meshmakers.Octo.Runtime.Engine.CrateDb;
using Meshmakers.Octo.Runtime.Engine.CrateDb.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using StreamDataException = Meshmakers.Octo.Runtime.Contracts.StreamData.StreamDataException;

namespace Meshmakers.Octo.Runtime.Engine.UnitTests;

/// <summary>
/// AB#6247 (N3) at the repository boundary: an archive the access re-validation set to Failed because columns reached a
/// Hidden attribute refuses every write, but its history stays queryable without those columns. Any other Failed archive
/// stays unreadable as before.
/// </summary>
public class CrateDbFailedOnHiddenColumnsReadGateTests
{
    private static readonly OctoObjectId Archive = OctoObjectId.GenerateNewId();
    private static readonly RtCkId<CkTypeId> SomeType = new("Test", new CkTypeId("Meter"));

    private readonly IStreamDataDatabaseClient _db = A.Fake<IStreamDataDatabaseClient>();
    private readonly IArchiveRuntimeStore _store = A.Fake<IArchiveRuntimeStore>();

    private static readonly IOptions<StreamDataConfiguration> Config =
        Options.Create(new StreamDataConfiguration { ConnectionString = "Host=ignored" });

    private CrateDbStreamDataRepository NewSut() =>
        new(NullLogger<CrateDbStreamDataRepository>.Instance, A.Fake<ICkCacheService>(), _db,
            A.Fake<IStreamDataDatabaseManagementClient>(), Config, "tenant-x", _store, A.Fake<IFormulaEngine>(),
            A.Fake<IRollupArchiveRuntimeStore>());

    private void Stub(CkArchiveStatus status, params string[] hidden) =>
        A.CallTo(() => _store.GetAsync(Archive)).Returns(
            new ArchiveSnapshot(Archive, SomeType, status, "meter",
                [new CkArchiveColumnSpec("Name", Indexed: false, Required: false),
                 new CkArchiveColumnSpec("Pin", Indexed: false, Required: false)])
            {
                StatusReason = hidden.Length == 0 ? null : "column 'Pin' reaches the Hidden attribute 'Pin'",
                HiddenColumns = hidden
            });

    private void AssertNoSqlExecuted() =>
        A.CallTo(() => _db.GetDataAsync(A<string>._, A<string>._)).MustNotHaveHappened();

    [Fact]
    public async Task FailedOnHiddenColumns_RefusesAHiddenColumnByName_BeforeAnySql()
    {
        Stub(CkArchiveStatus.Failed, "pin");

        var ex = await Assert.ThrowsAsync<StreamDataException>(() => NewSut().ExecuteQueryAsync(Archive,
            StreamDataQueryOptions.Create().WithCkTypeId(SomeType).WithColumns(["Pin"])));

        Assert.IsNotType<ArchiveNotActivatedException>(ex);
        Assert.Contains("Pin", ex.Message);
        AssertNoSqlExecuted();
    }

    [Fact]
    public async Task FailedOnHiddenColumns_AcceptsTheVisibleColumns()
    {
        Stub(CkArchiveStatus.Failed, "Pin");

        // The gate lets the query through; the faked database client answers with no rows.
        var result = await NewSut().ExecuteQueryAsync(Archive,
            StreamDataQueryOptions.Create().WithCkTypeId(SomeType).WithColumns(["Name"]));

        Assert.Empty(result.Rows);
    }

    [Fact]
    public async Task FailedOnHiddenColumns_RefusesEveryWrite()
    {
        Stub(CkArchiveStatus.Failed, "Pin");

        await Assert.ThrowsAsync<ArchiveNotActivatedException>(() => NewSut().InsertAsync(Archive, new StreamDataPoint
        {
            RtId = OctoObjectId.GenerateNewId(), CkTypeId = SomeType, Timestamp = DateTime.UtcNow,
            Attributes = new Dictionary<string, object?> { ["Name"] = "a" }
        }));
    }

    [Fact]
    public async Task FailedForAnotherReason_StaysUnreadable()
    {
        Stub(CkArchiveStatus.Failed);

        await Assert.ThrowsAsync<ArchiveNotActivatedException>(() => NewSut().ExecuteQueryAsync(Archive,
            StreamDataQueryOptions.Create().WithCkTypeId(SomeType).WithColumns(["Name"])));
        AssertNoSqlExecuted();
    }
}
