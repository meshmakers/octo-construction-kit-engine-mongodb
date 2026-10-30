using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using FakeItEasy;
using Meshmakers.Octo.ConstructionKit.Contracts;
using Meshmakers.Octo.ConstructionKit.Contracts.Services;
using Meshmakers.Octo.Runtime.Contracts.Formulas;
using Meshmakers.Octo.Runtime.Contracts.StreamData;
using Meshmakers.Octo.Runtime.Engine.CrateDb;
using Meshmakers.Octo.Runtime.Engine.CrateDb.Configuration;
using Meshmakers.Octo.Runtime.Engine.CrateDb.Copy;
using Meshmakers.Octo.Runtime.Engine.CrateDb.Dtos;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace Meshmakers.Octo.Runtime.Engine.UnitTests.Copy;

/// <summary>
/// AB#5967 (slice AB#6967): the repository reports the staging copy only when its client can run a statement
/// once under a timeout of its own, and otherwise behaves like every repository before it.
/// </summary>
public class CrateRepositoryStagingCopyTests
{
    private static readonly OctoObjectId Archive = OctoObjectId.GenerateNewId();
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private readonly IArchiveRuntimeStore _store = A.Fake<IArchiveRuntimeStore>();

    private CrateDbStreamDataRepository Repository(IStreamDataDatabaseClient client) => new(
        NullLogger<CrateDbStreamDataRepository>.Instance, A.Fake<ICkCacheService>(), client,
        A.Fake<IStreamDataDatabaseManagementClient>(),
        Options.Create(new StreamDataConfiguration { ConnectionString = "Host=ignored" }), "tenant-x", _store,
        A.Fake<IFormulaEngine>());

    [Fact]
    public async Task WithAClientThatCannotCopy_TheRepositoryReportsNoSupport_AndTheMembersThrow()
    {
        IStreamDataRepository repo = Repository(A.Fake<IStreamDataDatabaseClient>());

        Assert.False(repo.SupportsStagingCopy);
        await Assert.ThrowsAsync<ArchiveCopyNotSupportedException>(() => repo.PlanArchiveCopyAsync(Archive, null, Ct));
        await Assert.ThrowsAsync<ArchiveCopyNotSupportedException>(() =>
            repo.DescribeArchiveTableAsync(Archive, ArchiveCopyTable.Data, Ct));
        await Assert.ThrowsAsync<ArchiveCopyNotSupportedException>(() => repo.GetStorageVersionAsync(Ct));
        await Assert.ThrowsAsync<ArchiveCopyNotSupportedException>(async () =>
        {
            await foreach (var _ in repo.CopyToStagingAsync(Archive, new ArchiveCopyPlan(Archive, [], []),
                               new ArchiveCopyStagingLocation("file:///tmp/x"), null, Ct))
            {
            }
        });
    }

    [Fact]
    public async Task WithAClientThatCopies_TheRepositorySupportsIt_AndPlansAnArchiveWithoutTable()
    {
        var client = new CopyCapableDatabaseClient();
        A.CallTo(() => _store.GetAsync(Archive)).Returns(new ArchiveSnapshot(Archive,
            new RtCkId<CkTypeId>("Test", new CkTypeId("TempSensor")), CkArchiveStatus.Disabled, "raw",
            new[] { new CkArchiveColumnSpec("Voltage", true, false) }));
        IStreamDataRepository repo = Repository(client);

        var plan = await repo.PlanArchiveCopyAsync(Archive, null, Ct);

        Assert.True(repo.SupportsStagingCopy);
        Assert.Equal(Archive, plan.ArchiveRtId);
        Assert.Empty(plan.Slices);
    }

    [Fact]
    public async Task AnUnknownArchive_IsReportedAsNotFound()
    {
        var client = new CopyCapableDatabaseClient();
        A.CallTo(() => _store.GetAsync(Archive)).Returns((ArchiveSnapshot?)null);

        await Assert.ThrowsAsync<ArchiveNotFoundException>(() => Repository(client).PlanArchiveCopyAsync(Archive, null, Ct));
    }

    [Fact]
    public void TheTimeoutOfANonCopyStatement_KeepsItsMessageAndItsInnerException()
    {
        var inner = new InvalidOperationException("driver");

        var ex = new StreamDataStatementTimeoutException(CrateStatementClass.Interactive, TimeSpan.FromSeconds(30),
            TimeSpan.FromSeconds(30.5), "SELECT 1 FROM t", inner);

        Assert.Equal("A Interactive statement was ended after 30.5 s; its budget is 30.0 s. Statement: SELECT 1 FROM t", ex.Message);
        Assert.Same(inner, ex.InnerException);
    }

    [Fact]
    public void TheTimeoutOfACopy_CarriesTheRedactedStatementAndNoInnerException()
    {
        var statement = CopyStatement.CopyTo("\"t\".\"archive_1\"", "timestamp", DateTime.UnixEpoch, DateTime.UnixEpoch.AddDays(1),
            new ArchiveCopyStagingLocation("s3://CANARYKEYID1234:CANARY/SECRET@host/b/p"));

        var ex = new StreamDataStatementTimeoutException(CrateStatementClass.Maintenance, TimeSpan.FromSeconds(1),
            TimeSpan.FromSeconds(2), statement.Redacted, null);

        Assert.DoesNotContain("CANARY", ex.Message);
        Assert.DoesNotContain("CANARY", ex.ToString());
        Assert.Null(ex.InnerException);
        Assert.Contains("s3://***@host/b/p", ex.Message);
    }

    [Fact]
    public void ACopyStatementException_HasNoInnerExceptionAndNoData()
    {
        var statement = CopyStatement.CopyFrom("\"t\".\"archive_1\"",
            new ArchiveCopyStagingLocation("s3://CANARYKEYID1234:CANARY/SECRET@host/b/p"), false);

        var ex = new StreamDataCopyStatementException(statement.Redacted, "XX000", statement.Scrub(
            "Cannot read s3://CANARYKEYID1234:CANARY/SECRET@host/b/p"));

        Assert.DoesNotContain("CANARY", ex.Message);
        Assert.DoesNotContain("CANARY", ex.ToString());
        Assert.Null(ex.InnerException);
        Assert.Empty(ex.Data);
        Assert.Equal("XX000", ex.SqlState);
    }

    /// <summary>A database client that can also run the statements of the staging copy; it answers them with nothing.</summary>
    private sealed class CopyCapableDatabaseClient : IStreamDataDatabaseClient, ICrateCopyClient
    {
        public Task<CrateStatementResult> ExecuteStatementAsync(string tenantId, CopyStatement statement,
            TimeSpan commandTimeout, CancellationToken cancellationToken) => Task.FromResult(CrateStatementResult.Empty);

        public Task<IReadOnlyList<Meshmakers.Octo.Runtime.Engine.CrateDb.Client.SkippedArchiveRow>> InsertDataAsync(string tenantId, string qualifiedTable, IReadOnlyList<string> userColumnNames,
            DataPointDto datapoint, IReadOnlyList<ArchiveConflictKey>? conflictPrecedence = null) => throw new NotSupportedException();

        public Task<IReadOnlyList<Meshmakers.Octo.Runtime.Engine.CrateDb.Client.SkippedArchiveRow>> InsertDataAsync(string tenantId, string qualifiedTable, IReadOnlyList<string> userColumnNames,
            IEnumerable<DataPointDto> datapoints, IReadOnlyList<ArchiveConflictKey>? conflictPrecedence = null) =>
            throw new NotSupportedException();

        public Task<IReadOnlyList<Meshmakers.Octo.Runtime.Engine.CrateDb.Client.SkippedArchiveRow>> InsertTimeRangeDataAsync(string tenantId, string qualifiedTable, IReadOnlyList<string> userColumnNames,
            IEnumerable<TimeRangeDataPointDto> datapoints, bool generationTracked = false,
            IReadOnlyList<ArchiveConflictKey>? conflictPrecedence = null) => throw new NotSupportedException();

        public Task<List<DataPointDto>> GetDataAsync(string tenantId, string query) => throw new NotSupportedException();

        public IAsyncEnumerable<IReadOnlyDictionary<string, object?>> StreamRawRowsAsync(string tenantId, string query,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public Task<long> GetCountAsync(string tenantId, string countQuery) => throw new NotSupportedException();

        public Task<long> GetCountAsync(string tenantId, string countQuery, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<long> GetCountAsync(string tenantId, string countQuery, CrateStatementClass statementClass,
            CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<int> ExecuteNonQueryAsync(string tenantId, string sql, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<int> ExecuteNonQueryAsync(string tenantId, string sql, CrateStatementClass statementClass,
            CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task ExecuteBulkAsync(string tenantId, string parameterizedSql,
            IReadOnlyList<IReadOnlyList<object?>> argumentSets, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task RefreshArchiveTableAsync(string tenantId, string qualifiedTable) => throw new NotSupportedException();

        public Task RefreshArchiveTableAsync(string tenantId, string qualifiedTable, CrateStatementClass statementClass,
            CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<IReadOnlyList<CrateTableStatsRow>> GetTableStatsAsync(string tenantId, IReadOnlyList<string> tableNames,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }
}
