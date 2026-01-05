using Meshmakers.Octo.ConstructionKit.Contracts;
using Meshmakers.Octo.Runtime.Contracts.Repositories.Query;
using Meshmakers.Octo.Runtime.Engine.MongoDb.IntegrationTests.Fixtures;

using Xunit;

namespace Meshmakers.Octo.Runtime.Engine.MongoDb.IntegrationTests;

[Collection("Sequential")]
public class GetRtDeepGraphAsyncTests
    : IClassFixture<SampleRtModelDataFixture>
{
    private readonly SampleRtModelDataFixture _sampleRtModelDataFixture;

    public GetRtDeepGraphAsyncTests(SampleRtModelDataFixture sampleRtModelDataFixture, ITestOutputHelper output)
    {
        _sampleRtModelDataFixture = sampleRtModelDataFixture;
        sampleRtModelDataFixture.OutputHelper = output;
    }

    [Fact]
    public async Task GetSubgraphAsync_Default_OK()
    {
        var systemContext = _sampleRtModelDataFixture.GetSystemContext();
        var tenantRepository = systemContext.GetTenantRepository();
        using var session = await tenantRepository.GetSessionAsync();
        session.StartTransaction();

        var queryOptions = RtEntityQueryOptions.Create();

        var resultSet = await tenantRepository.GetRtDeepGraphAsync(session, [
                new OctoObjectId("66803ecf4aa85720dda96a97"),
            ],
            new RtCkId<CkTypeId>("Test/Continent"), queryOptions);

        await session.CommitTransactionAsync();

        Assert.Equal(23, resultSet.TotalCount); // +2 for Room and TechnicalRoom
    }

    [Fact]
    public async Task GetSubgraphAsync_Default_IncludeDeleted_OK()
    {
        var systemContext = _sampleRtModelDataFixture.GetSystemContext();
        var tenantRepository = systemContext.GetTenantRepository();
        using var session = await tenantRepository.GetSessionAsync();
        session.StartTransaction();

        var queryOptions = RtEntityQueryOptions.Create().Global(true);

        var resultSet = await tenantRepository.GetRtDeepGraphAsync(session, [
                new OctoObjectId("66803ecf4aa85720dda96a97"),
            ],
            new RtCkId<CkTypeId>("Test/Continent"), queryOptions);

        await session.CommitTransactionAsync();

        Assert.Equal(27, resultSet.TotalCount); // +2 for Room and TechnicalRoom
    }

    [Fact]
    public async Task GetSubgraphAsync_NoRelationships_OK()
    {
        var systemContext = _sampleRtModelDataFixture.GetSystemContext();
        var tenantRepository = systemContext.GetTenantRepository();
        using var session = await tenantRepository.GetSessionAsync();
        session.StartTransaction();

        var queryOptions = RtEntityQueryOptions.Create();

        var resultSet = await tenantRepository.GetRtDeepGraphAsync(session, [
                new OctoObjectId("66803ecf4aa85720dda96b09"),
            ],
            new RtCkId<CkTypeId>("Test/HouseHold"), queryOptions);

        await session.CommitTransactionAsync();

        Assert.Equal(4, resultSet.TotalCount); // +2 for Room and TechnicalRoom
        Assert.Contains(new OctoObjectId("66803ecf4aa85720dda96b09"), resultSet.Items.Select(x => x.Id.RtId));
        Assert.Contains(new OctoObjectId("66803ecf4aa85720dda96b11"), resultSet.Items.Select(x => x.Id.RtId));
        Assert.Contains(new OctoObjectId("68fded922b85e5d74c05a567"), resultSet.Items.Select(x => x.Id.RtId)); // Room
        Assert.Contains(new OctoObjectId("68fded922b85e5d74c05a568"), resultSet.Items.Select(x => x.Id.RtId)); // TechnicalRoom
    }

    [Fact]
    public async Task GetSubgraphAsync_Paging_OK()
    {
        var systemContext = _sampleRtModelDataFixture.GetSystemContext();
        var tenantRepository = systemContext.GetTenantRepository();
        using var session = await tenantRepository.GetSessionAsync();
        session.StartTransaction();

        var queryOptions = RtEntityQueryOptions.Create();

        var resultSet = await tenantRepository.GetRtDeepGraphAsync(session, [
                new OctoObjectId("66803ecf4aa85720dda96a97"),
            ],
            new RtCkId<CkTypeId>("Test/Continent"), queryOptions, 1, 2);

        await session.CommitTransactionAsync();

        Assert.Equal(23, resultSet.TotalCount); // +2 for Room and TechnicalRoom
        Assert.Equal(2, resultSet.Items.Count());
    }

    [Fact]
    public async Task GetSubgraphAsync_MultipleOriginRtIds_OK()
    {
        var systemContext = _sampleRtModelDataFixture.GetSystemContext();
        var tenantRepository = systemContext.GetTenantRepository();
        using var session = await tenantRepository.GetSessionAsync();
        session.StartTransaction();

        var queryOptions = RtEntityQueryOptions.Create();

        var resultSet = await tenantRepository.GetRtDeepGraphAsync(session, [
                new OctoObjectId("66803ecf4aa85720dda96b07"),
                new OctoObjectId("66803ecf4aa85720dda96b08"),
            ],
            new RtCkId<CkTypeId>("Test/Municipality"), queryOptions);

        await session.CommitTransactionAsync();

        Assert.Equal(11, resultSet.TotalCount); // +2 for Room and TechnicalRoom
    }

    [Fact]
    public async Task GetSubgraphAsync_SingleEntity_OK()
    {
        var systemContext = _sampleRtModelDataFixture.GetSystemContext();
        var tenantRepository = systemContext.GetTenantRepository();
        using var session = await tenantRepository.GetSessionAsync();
        session.StartTransaction();

        var queryOptions = RtEntityQueryOptions.Create();

        var resultSet = await tenantRepository.GetRtDeepGraphAsync(session, [
                new OctoObjectId("66803ecf4aa85720dda96b11"),
            ],
            new RtCkId<CkTypeId>("Test/MeasuringPoint"), queryOptions);

        await session.CommitTransactionAsync();

        Assert.Equal(1, resultSet.TotalCount);
    }
}
