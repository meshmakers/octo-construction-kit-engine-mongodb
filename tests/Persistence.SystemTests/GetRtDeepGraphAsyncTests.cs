using Meshmakers.Octo.ConstructionKit.Contracts;
using Meshmakers.Octo.Runtime.Contracts.Repositories.Query;
using Meshmakers.Octo.SystematizedData.Persistence.SystemTests.Fixtures;

using Xunit;

namespace Meshmakers.Octo.SystematizedData.Persistence.SystemTests;

[Collection("Sequential")]
public class GetRtDeepGraphAsyncTests
    : IClassFixture<GenerateSampleDataFixture>
{
    private readonly GenerateSampleDataFixture _generateSampleDataFixture;

    public GetRtDeepGraphAsyncTests(GenerateSampleDataFixture generateSampleDataFixture, ITestOutputHelper output)
    {
        _generateSampleDataFixture = generateSampleDataFixture;
        generateSampleDataFixture.OutputHelper = output;
    }

    [Fact]
    public async Task GetSubgraphAsync_Default_OK()
    {
        var systemContext = _generateSampleDataFixture.GetSystemContext();
        var tenantRepository = systemContext.GetTenantRepository();
        using var session = await tenantRepository.GetSessionAsync();
        session.StartTransaction();

        var dataOperation = DataQueryOperation.Create();

        var resultSet = await tenantRepository.GetRtDeepGraphAsync(session, [
                new OctoObjectId("66803ecf4aa85720dda96a97"),
            ],
            new CkId<CkTypeId>("Test/Continent"), dataOperation);

        await session.CommitTransactionAsync();

        Assert.Equal(18, resultSet.TotalCount);
    }

    [Fact]
    public async Task GetSubgraphAsync_NoRelationships_OK()
    {
        var systemContext = _generateSampleDataFixture.GetSystemContext();
        var tenantRepository = systemContext.GetTenantRepository();
        using var session = await tenantRepository.GetSessionAsync();
        session.StartTransaction();

        var dataOperation = DataQueryOperation.Create();

        var resultSet = await tenantRepository.GetRtDeepGraphAsync(session, [
                new OctoObjectId("66803ecf4aa85720dda96b09"),
            ],
            new CkId<CkTypeId>("Test/HouseHold"), dataOperation);

        await session.CommitTransactionAsync();

        Assert.Equal(2, resultSet.TotalCount);
        Assert.Contains(new OctoObjectId("66803ecf4aa85720dda96b09"), resultSet.Items.Select(x => x.Id.RtId));
        Assert.Contains(new OctoObjectId("66803ecf4aa85720dda96b11"), resultSet.Items.Select(x => x.Id.RtId));
    }

    [Fact]
    public async Task GetSubgraphAsync_Paging_OK()
    {
        var systemContext = _generateSampleDataFixture.GetSystemContext();
        var tenantRepository = systemContext.GetTenantRepository();
        using var session = await tenantRepository.GetSessionAsync();
        session.StartTransaction();

        var dataOperation = DataQueryOperation.Create();

        var resultSet = await tenantRepository.GetRtDeepGraphAsync(session, [
                new OctoObjectId("66803ecf4aa85720dda96a97"),
            ],
            new CkId<CkTypeId>("Test/Continent"), dataOperation, 1, 2);

        await session.CommitTransactionAsync();

        Assert.Equal(18, resultSet.TotalCount);
        Assert.Equal(2, resultSet.Items.Count());
    }

    [Fact]
    public async Task GetSubgraphAsync_MultipleOriginRtIds_OK()
    {
        var systemContext = _generateSampleDataFixture.GetSystemContext();
        var tenantRepository = systemContext.GetTenantRepository();
        using var session = await tenantRepository.GetSessionAsync();
        session.StartTransaction();

        var dataOperation = DataQueryOperation.Create();

        var resultSet = await tenantRepository.GetRtDeepGraphAsync(session, [
                new OctoObjectId("66803ecf4aa85720dda96b07"),
                new OctoObjectId("66803ecf4aa85720dda96b08"),
            ],
            new CkId<CkTypeId>("Test/Municipality"), dataOperation);

        await session.CommitTransactionAsync();

        Assert.Equal(9, resultSet.TotalCount);
    }

    [Fact]
    public async Task GetSubgraphAsync_SingleEntity_OK()
    {
        var systemContext = _generateSampleDataFixture.GetSystemContext();
        var tenantRepository = systemContext.GetTenantRepository();
        using var session = await tenantRepository.GetSessionAsync();
        session.StartTransaction();

        var dataOperation = DataQueryOperation.Create();

        var resultSet = await tenantRepository.GetRtDeepGraphAsync(session, [
                new OctoObjectId("66803ecf4aa85720dda96b11"),
            ],
            new CkId<CkTypeId>("Test/MeasuringPoint"), dataOperation);

        await session.CommitTransactionAsync();

        Assert.Equal(1, resultSet.TotalCount);
    }
}
