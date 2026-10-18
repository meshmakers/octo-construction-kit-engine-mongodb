using System;
using System.Threading;
using System.Threading.Tasks;
using FakeItEasy;
using Meshmakers.Octo.ConstructionKit.Contracts.Services;
using Meshmakers.Octo.Runtime.Contracts.Formulas;
using Meshmakers.Octo.Runtime.Contracts.MongoDb.Configuration;
using Meshmakers.Octo.Runtime.Contracts.MongoDb.Services;
using Meshmakers.Octo.Runtime.Contracts.StreamData;
using Meshmakers.Octo.Runtime.Engine.CrateDb;
using Meshmakers.Octo.Runtime.Engine.CrateDb.Configuration;
using Meshmakers.Octo.Runtime.Engine.CrateDb.Copy;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace Meshmakers.Octo.Runtime.Engine.UnitTests.Copy;

/// <summary>
/// AB#5967 / AB#6969: the readiness answer of the storage is one piece of code behind two doors - the tenant-bound
/// repository and the cluster-level factory. Against the simulated CrateDB: filter set / not set, a failing
/// statement, a storage that cannot copy, and that the probe only reads.
/// </summary>
public class StagingCopyReadinessProbeTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private readonly SimulatedCrate _crate = new();

    private Task<StagingCopyReadiness> CheckAsync()
        => StagingCopyReadinessProbe.CheckAsync(NullLogger.Instance, StagingCopyReadinessProbe.ClusterConnectionLabel, _crate, Ct);

    [Fact]
    public async Task FilterSet_IsReady_AndCarriesTheStorageVersion()
    {
        var readiness = await CheckAsync();

        Assert.True(readiness.IsReady);
        Assert.True(readiness.Supported);
        Assert.True(readiness.JobLogFilterSet);
        Assert.Equal("5.10.10", readiness.StorageVersion);
        Assert.Null(readiness.Reason);
    }

    [Fact]
    public async Task FilterMissing_IsNotReady_AndTheReasonSaysWhatToSet()
    {
        _crate.Filter = "true";

        var readiness = await CheckAsync();

        Assert.False(readiness.IsReady);
        Assert.True(readiness.Supported);
        Assert.False(readiness.JobLogFilterSet);
        Assert.Contains("stats.jobs_log_filter", readiness.Reason, StringComparison.Ordinal);
        Assert.Contains("SET GLOBAL PERSISTENT", readiness.Reason, StringComparison.Ordinal);
    }

    [Fact]
    public async Task OnlyReadsClusterState_NothingIsWritten()
    {
        _crate.Filter = "true";

        await CheckAsync();
        _crate.Filter = JobLogFilterGuard.RecommendedFilter;
        await CheckAsync();

        Assert.Equal(4, _crate.Executed.Count); // setting + version, twice
        Assert.All(_crate.Executed, sql => Assert.StartsWith("SELECT ", sql, StringComparison.Ordinal));
        Assert.DoesNotContain(_crate.Executed, sql => sql.Contains("SET GLOBAL", StringComparison.OrdinalIgnoreCase));
        Assert.Equal(JobLogFilterGuard.RecommendedFilter, _crate.Filter);
    }

    [Fact]
    public async Task FailingSettingRead_IsAnAnswer_NotAnException_AndKeepsSecretsOut()
    {
        _crate.Fault = statement => throw new StreamDataCopyStatementException(
            statement.Redacted, "XX000", "The node is not reachable.");

        var readiness = await CheckAsync();

        Assert.False(readiness.IsReady);
        Assert.True(readiness.Supported);
        Assert.Null(readiness.StorageVersion);
        Assert.Contains("could not be asked", readiness.Reason, StringComparison.Ordinal);
        Assert.Contains("not reachable", readiness.Reason, StringComparison.Ordinal);
    }

    [Fact]
    public async Task FailingVersionRead_DoesNotChangeTheAnswer_TheFilterDecides()
    {
        _crate.Fault = statement =>
        {
            if (statement.Reveal().Contains("version['number']", StringComparison.Ordinal))
            {
                throw new StreamDataCopyStatementException(statement.Redacted, null, "No version.");
            }
        };

        var readiness = await CheckAsync();

        Assert.True(readiness.IsReady);
        Assert.Null(readiness.StorageVersion);
    }

    [Fact]
    public async Task StorageWithoutCopyClient_IsNotSupported()
    {
        var readiness = await StagingCopyReadinessProbe.CheckAsync(NullLogger.Instance, "default", null, Ct);

        Assert.False(readiness.IsReady);
        Assert.False(readiness.Supported);
        Assert.NotNull(readiness.Reason);
    }

    [Fact]
    public async Task Cancellation_IsNotSwallowed()
    {
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            StagingCopyReadinessProbe.CheckAsync(NullLogger.Instance, "default", _crate, cts.Token));
    }

    [Fact]
    public async Task FactoryOverAStorageThatCannotCopy_AnswersNotSupported_WithoutATenant()
    {
        IStreamDataRepositoryFactory factory = new CrateDbStreamDataRepositoryFactory(
            NullLoggerFactory.Instance, A.Fake<ICkCacheService>(), A.Fake<IStreamDataDatabaseClient>(),
            A.Fake<IStreamDataDatabaseManagementClient>(), Options.Create(new StreamDataConfiguration { ConnectionString = "Host=unused" }),
            A.Fake<IFormulaEngine>());

        var readiness = await factory.CheckStagingCopyReadinessAsync(Ct);

        Assert.False(readiness.IsReady);
        Assert.False(readiness.Supported);
    }

    [Fact]
    public async Task ContractDefault_AnswersNotSupported()
    {
        var factory = A.Fake<IStreamDataRepositoryFactory>(o => o.CallsBaseMethods());

        var readiness = await factory.CheckStagingCopyReadinessAsync(Ct);

        Assert.False(readiness.IsReady);
        Assert.False(readiness.Supported);
        Assert.False(string.IsNullOrWhiteSpace(readiness.Reason));
    }
}
