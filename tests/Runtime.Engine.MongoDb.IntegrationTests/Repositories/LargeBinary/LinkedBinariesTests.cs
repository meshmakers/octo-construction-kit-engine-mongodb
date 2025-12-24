using Meshmakers.Octo.ConstructionKit.Contracts;
using Meshmakers.Octo.Runtime.Contracts;
using Meshmakers.Octo.Runtime.Contracts.MongoDb.Repositories;
using Meshmakers.Octo.Runtime.Contracts.Repositories;
using Meshmakers.Octo.Runtime.Contracts.RepositoryEntities;
using Meshmakers.Octo.Runtime.Engine.MongoDb.IntegrationTests.Fixtures;

using TestCkModel.Generated.Test.v1;

using Xunit;

namespace Meshmakers.Octo.Runtime.Engine.MongoDb.IntegrationTests.Repositories.LargeBinary;

[Collection("Sequential")]
public class LinkedBinariesTests(ImportTestCkModelFixture fixture) : IClassFixture<ImportTestCkModelFixture>
{
    [Fact]
    public async Task FileSystem_InsertOneRtEntityAsync()
    {
        var systemContext = fixture.GetSystemContext();
        var tenantRepository = systemContext.GetTenantRepository();

        var (session, binaryEntity) = await InsertCustomers(tenantRepository);

        await session.CommitTransactionAsync();

        var r = await GetRtBinaryEntity(tenantRepository, binaryEntity);

        Assert.NotNull(r);

    }

    [Fact]
    public async Task FileSystem_DeleteOneRtEntityAsync()
    {
        var systemContext = fixture.GetSystemContext();
        var tenantRepository = systemContext.GetTenantRepository();

        var (session, binaryEntity) = await InsertCustomers(tenantRepository);

        await tenantRepository.DeleteOneRtEntityByRtIdAsync<RtBinaryEntity>(session, binaryEntity.RtId, DeleteOptions.Erase);

        await session.CommitTransactionAsync();
    }

    [Fact]
    public async Task FileSystem_ReplaceOneRtEntityByIdAsync()
    {
        var systemContext = fixture.GetSystemContext();
        var tenantRepository = systemContext.GetTenantRepository();

        var (session, binaryEntity) = await InsertCustomers(tenantRepository);

        await session.CommitTransactionAsync();

        var filePath = "testData/largeBinaries/Products.pdf";
        var stream = File.OpenRead(filePath);

        using var replaceSession = await tenantRepository.GetSessionAsync();
        replaceSession.StartTransaction();

        var replaceBinaryEntity = new RtBinaryEntity
        {
            DataCount = 7,
            Binary = new EntityBinaryInfo
            {
                Filename = "Products.pdf",
                ContentType = "application/pdf",
                Stream = stream
            }
        };

        await tenantRepository.ReplaceOneRtEntityByIdAsync(replaceSession, binaryEntity.RtId, replaceBinaryEntity);

        await replaceSession.CommitTransactionAsync();

        var r = await GetRtBinaryEntity(tenantRepository, binaryEntity);

        Assert.NotNull(r);
        Assert.Equal(replaceBinaryEntity.RtId, r.RtId);
        Assert.Equal("Products.pdf", r.Binary.Filename);
        Assert.Equal("application/pdf", r.Binary.ContentType);
        Assert.Equal(56987, r.Binary.Size);
    }

    [Fact]
    public async Task FileSystem_UpdateOneRtEntityByIdAsync()
    {
        var systemContext = fixture.GetSystemContext();
        var tenantRepository = systemContext.GetTenantRepository();

        var (session, binaryEntity) = await InsertCustomers(tenantRepository);

        await session.CommitTransactionAsync();

        var filePath = "testData/largeBinaries/Products.pdf";
        var stream = File.OpenRead(filePath);

        using var replaceSession = await tenantRepository.GetSessionAsync();
        replaceSession.StartTransaction();

        var replaceBinaryEntity = new RtBinaryEntity
        {
            Binary = new EntityBinaryInfo
            {
                Filename = "Products.pdf",
                ContentType = "application/pdf",
                Stream = stream
            }
        };

        await tenantRepository.UpdateOneRtEntityByIdAsync(replaceSession, binaryEntity.RtId, replaceBinaryEntity);

        await replaceSession.CommitTransactionAsync();

        var r = await GetRtBinaryEntity(tenantRepository, binaryEntity);

        Assert.NotNull(r);
        Assert.Equal(replaceBinaryEntity.RtId, r.RtId);
        Assert.Equal(replaceBinaryEntity.Binary.BinaryId, r.Binary.BinaryId);
        Assert.Equal("Products.pdf", r.Binary.Filename);
        Assert.Equal("application/pdf", r.Binary.ContentType);
        Assert.Equal(56987, r.Binary.Size);
    }


    private static async Task<(IOctoSession session, RtBinaryEntity binaryEntity)> InsertCustomers(ITenantRepository tenantRepository)
    {
        var filePath = "testData/largeBinaries/Customers.xlsx";
        var stream = File.OpenRead(filePath);

        using var session = await tenantRepository.GetSessionAsync();
        session.StartTransaction();

        RtBinaryEntity binaryEntity = new()
        {
            RtId = OctoObjectId.GenerateNewId(),
            DataCount = 5,
            Binary = new EntityBinaryInfo
            {
                Filename = "Customers.xlsx",
                ContentType = "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet",
                Stream = stream
            }
        };

        await tenantRepository.InsertOneRtEntityAsync(session, binaryEntity);
        return (session, binaryEntity);
    }

    private static async Task<RtBinaryEntity?> GetRtBinaryEntity(ITenantRepository tenantRepository,
        RtBinaryEntity binaryEntity)
    {
        using var sessionRead = await tenantRepository.GetSessionAsync();
        sessionRead.StartTransaction();

        var r = await tenantRepository.GetRtEntityByRtIdAsync<RtBinaryEntity>(sessionRead, binaryEntity.RtId);

        await sessionRead.CommitTransactionAsync();
        return r;
    }
}
