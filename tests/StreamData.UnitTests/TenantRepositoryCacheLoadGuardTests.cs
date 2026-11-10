using FakeItEasy;
using Meshmakers.Common.Metrics.Context;
using Meshmakers.Octo.ConstructionKit.Contracts;
using Meshmakers.Octo.ConstructionKit.Contracts.Services;
using Meshmakers.Octo.Runtime.Contracts;
using Meshmakers.Octo.Runtime.Engine.MongoDb.Repositories.MongoDb;
using Meshmakers.Octo.Runtime.Engine.MongoDb.Services;
using Meshmakers.Octo.Runtime.Engine.Repositories;
using Xunit;

namespace Meshmakers.Octo.Runtime.Engine.MongoDb.UnitTests;

/// <summary>
///     AB#8677: when the CK cache load of a tenant fails, the lookup of the running CK model imports only classifies the
///     failure. A lookup that fails itself must never replace the load's <see cref="ModelValidationException" />.
/// </summary>
public class TenantRepositoryCacheLoadGuardTests
{
    private readonly IMongoDbRepositoryDataSource _dataSource = A.Fake<IMongoDbRepositoryDataSource>();
    private readonly IModelLoaderService _modelLoader = A.Fake<IModelLoaderService>();
    private readonly ModelValidationException _loadFailure = new("Dependency 'Basic' could not be resolved.");

    public TenantRepositoryCacheLoadGuardTests()
    {
        A.CallTo(() => _modelLoader.LoadAsync(A<string>._, A<IOctoSession>._, A<IMongoDbRepositoryDataSource>._))
            .ThrowsAsync(_loadFailure);
    }

    private Task LoadAsync()
    {
        var repository = new TenantRepository(
            "tenant-x", A.Fake<IMetricsContext>(), A.Fake<ICkCacheService>(), _modelLoader, _dataSource,
            A.Fake<IBulkRtMutation>());
        return repository.LoadCacheForTenantAsync(A.Fake<ICkCacheService>());
    }

    [Fact]
    public async Task LookupOfRunningImportsFails_TheLoadFailureIsThrownUnmarked()
    {
        A.CallTo(() => _dataSource.GetModelsBeingImportedAsync(A<CancellationToken>._))
            .ThrowsAsync(new InvalidOperationException("MongoDB is not reachable."));

        var thrown = await Assert.ThrowsAsync<ModelValidationException>(LoadAsync);

        Assert.Same(_loadFailure, thrown);
        Assert.False(CkModelImportInProgress.IsMarked(thrown));
        A.CallTo(() => _modelLoader.LoadAsync(A<string>._, A<IOctoSession>._, A<IMongoDbRepositoryDataSource>._))
            .MustHaveHappenedOnceExactly();
    }

    [Fact]
    public async Task WaitForARunningImportFails_TheLoadFailureIsThrownMarked()
    {
        A.CallTo(() => _dataSource.GetModelsBeingImportedAsync(A<CancellationToken>._))
            .Returns(Task.FromResult<IReadOnlyList<string>>(["Basic"])).Once()
            .Then.ThrowsAsync(new InvalidOperationException("MongoDB is not reachable."));

        var thrown = await Assert.ThrowsAsync<ModelValidationException>(LoadAsync);

        Assert.Same(_loadFailure, thrown);
        Assert.True(CkModelImportInProgress.IsMarked(thrown));
        Assert.Equal("Basic", thrown.Data[CkModelImportInProgress.ExceptionDataKey]);
    }
}
