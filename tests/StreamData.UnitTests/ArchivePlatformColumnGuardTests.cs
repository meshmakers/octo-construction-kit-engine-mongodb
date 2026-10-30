using System.Runtime.CompilerServices;
using FakeItEasy;
using Meshmakers.Octo.ConstructionKit.Contracts;
using Meshmakers.Octo.Runtime.Contracts.StreamData;
using Meshmakers.Octo.Runtime.Engine.CrateDb;
using Microsoft.Extensions.Logging.Abstractions;

namespace Meshmakers.Octo.Runtime.Engine.UnitTests;

/// <summary>
/// AB#7727: an archive table gets its platform columns on its own, once per table and process, and a replica that
/// loses the race for the ALTER carries on.
/// </summary>
public class ArchivePlatformColumnGuardTests
{
    private const string Tenant = "tenant-x";
    private static readonly RtCkId<CkTypeId> Type = new("Test", new CkTypeId("Meter"));

    private readonly IStreamDataDatabaseClient _db = A.Fake<IStreamDataDatabaseClient>();
    private readonly IStreamDataDatabaseManagementClient _management = A.Fake<IStreamDataDatabaseManagementClient>();
    private readonly RecomputeProcessState _state = new();
    private readonly OctoObjectId _archive = OctoObjectId.GenerateNewId();

    private ArchivePlatformColumnGuard NewSut() => new(Tenant, _db, _management, _state, NullLogger.Instance);

    private void StubColumns(params string[] names) =>
        A.CallTo(() => _db.StreamRawRowsAsync(Tenant, A<string>.That.Contains("information_schema.columns"), A<CancellationToken>._))
            .ReturnsLazily(() => Rows(names));

    private static async IAsyncEnumerable<IReadOnlyDictionary<string, object?>> Rows(
        string[] names, [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        await Task.CompletedTask;
        foreach (var name in names)
        {
            cancellationToken.ThrowIfCancellationRequested();
            yield return new Dictionary<string, object?> { ["column_name"] = name };
        }
    }

    private ArchiveSnapshot Rollup() => new(_archive, Type, CkArchiveStatus.Activated, null, [])
    {
        RollupAggregations = [new CkRollupAggregationSpec("Value", CkRollupFunction.Sum, null)]
    };

    private ArchiveSnapshot TimeRange() => new(_archive, Type, CkArchiveStatus.Activated, null, []) { IsTimeRange = true };

    [Fact]
    public async Task ARollupTableWithoutTheColumns_GetsThem_NullableWithoutADefault()
    {
        StubColumns("window_start");

        await NewSut().EnsureAsync(Rollup(), CancellationToken.None);

        A.CallTo(() => _management.ExecuteDdlAsync(Tenant, A<string>.That.EndsWith("ADD COLUMN \"manual_count\" BIGINT;"), A<CancellationToken>._))
            .MustHaveHappenedOnceExactly();
        A.CallTo(() => _management.ExecuteDdlAsync(Tenant, A<string>.That.EndsWith("ADD COLUMN \"last_manual_at\" TIMESTAMP WITH TIME ZONE;"), A<CancellationToken>._))
            .MustHaveHappenedOnceExactly();
    }

    [Fact]
    public async Task ATimeRangeTable_GetsTheRowOriginColumns_NotTheRollupColumns()
    {
        StubColumns("window_start");

        await NewSut().EnsureAsync(TimeRange(), CancellationToken.None);

        A.CallTo(() => _management.ExecuteDdlAsync(Tenant, A<string>.That.Contains("\"row_origin\""), A<CancellationToken>._))
            .MustHaveHappenedOnceExactly();
        A.CallTo(() => _management.ExecuteDdlAsync(Tenant, A<string>.That.Contains("\"manual_count\""), A<CancellationToken>._))
            .MustNotHaveHappened();
    }

    [Fact]
    public async Task TheCheckRunsOncePerTableAndProcess_AlsoAcrossGuardInstances()
    {
        StubColumns("window_start", "manual_count", "last_manual_at");

        await NewSut().EnsureRollupColumnsAsync(_archive.ToString(), CancellationToken.None);
        await NewSut().EnsureRollupColumnsAsync(_archive.ToString(), CancellationToken.None);

        A.CallTo(() => _db.StreamRawRowsAsync(Tenant, A<string>._, A<CancellationToken>._)).MustHaveHappenedOnceExactly();
        A.CallTo(() => _management.ExecuteDdlAsync(Tenant, A<string>._, A<CancellationToken>._)).MustNotHaveHappened();
    }

    [Fact]
    public async Task AColumnAddedConcurrentlyByAnotherReplica_IsBenign()
    {
        StubColumns("window_start");
        A.CallTo(() => _management.ExecuteDdlAsync(Tenant, A<string>._, A<CancellationToken>._))
            .Throws(new InvalidOperationException("The table already has a column named manual_count"));

        await NewSut().EnsureRollupColumnsAsync(_archive.ToString(), CancellationToken.None);

        Assert.True(_state.EnsuredOriginColumns.ContainsKey(TenantSchema.QualifiedArchiveTable(Tenant, _archive.ToString())));
    }

    [Fact]
    public async Task AnOtherDdlFailure_IsNotSwallowed_AndNotRemembered()
    {
        StubColumns("window_start");
        A.CallTo(() => _management.ExecuteDdlAsync(Tenant, A<string>._, A<CancellationToken>._))
            .Throws(new InvalidOperationException("cluster state update timed out"));

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => NewSut().EnsureRollupColumnsAsync(_archive.ToString(), CancellationToken.None));

        Assert.False(_state.EnsuredOriginColumns.ContainsKey(TenantSchema.QualifiedArchiveTable(Tenant, _archive.ToString())));
    }

    [Fact]
    public async Task ATableThatIsNotThere_IsLeftToTheStatement_AndAskedForAgain()
    {
        StubColumns();

        await NewSut().EnsureRollupColumnsAsync(_archive.ToString(), CancellationToken.None);

        A.CallTo(() => _management.ExecuteDdlAsync(Tenant, A<string>._, A<CancellationToken>._)).MustNotHaveHappened();
        Assert.False(_state.EnsuredOriginColumns.ContainsKey(TenantSchema.QualifiedArchiveTable(Tenant, _archive.ToString())));
    }
}
