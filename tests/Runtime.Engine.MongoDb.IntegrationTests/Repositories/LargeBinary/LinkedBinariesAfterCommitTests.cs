using Meshmakers.Octo.ConstructionKit.Contracts;
using Meshmakers.Octo.Runtime.Contracts;
using Meshmakers.Octo.Runtime.Contracts.MongoDb;
using Meshmakers.Octo.Runtime.Contracts.MongoDb.Repositories;
using Meshmakers.Octo.Runtime.Contracts.Repositories;
using Meshmakers.Octo.Runtime.Contracts.RepositoryEntities;
using Meshmakers.Octo.Runtime.Engine.MongoDb.IntegrationTests.Collections;
using Meshmakers.Octo.Runtime.Engine.MongoDb.IntegrationTests.Fixtures;

using TestCkModel.Generated.Test.v1;

using Xunit;

namespace Meshmakers.Octo.Runtime.Engine.MongoDb.IntegrationTests.Repositories.LargeBinary;

/// <summary>
///     AB#6248: GridFS bytes of linked binaries are deleted/replaced only after the transaction has committed.
/// </summary>
[Collection(ImportTestCkModelCollection.Name)]
public class LinkedBinariesAfterCommitTests(ImportTestCkModelFixture fixture)
{
    private const long CustomersSize = 5401;
    private const long ProductsSize = 56987;

    [Fact]
    public async Task Delete_BytesRemainUntilCommit_ThenAreRemoved()
    {
        var tenantRepository = fixture.GetSystemContext().GetTenantRepository();
        var entity = await InsertCommittedAsync(tenantRepository);
        var binaryId = entity.Binary.BinaryId!.Value;

        using var session = await tenantRepository.GetSessionAsync();
        session.StartTransaction();
        await tenantRepository.DeleteOneRtEntityByRtIdAsync<RtBinaryEntity>(session, entity.RtId, DeleteOptions.Erase);

        Assert.True(await BinaryExistsAsync(tenantRepository, binaryId), "bytes must survive until commit");

        await session.CommitTransactionAsync();

        Assert.False(await BinaryExistsAsync(tenantRepository, binaryId), "bytes must be gone after commit");
    }

    [Fact]
    public async Task Delete_Rollback_KeepsBytesAndEntityReadable()
    {
        var tenantRepository = fixture.GetSystemContext().GetTenantRepository();
        var entity = await InsertCommittedAsync(tenantRepository);
        var binaryId = entity.Binary.BinaryId!.Value;

        using (var session = await tenantRepository.GetSessionAsync())
        {
            session.StartTransaction();
            await tenantRepository.DeleteOneRtEntityByRtIdAsync<RtBinaryEntity>(session, entity.RtId,
                DeleteOptions.Erase);
            await session.AbortTransactionAsync();
        }

        Assert.True(await BinaryExistsAsync(tenantRepository, binaryId));
        Assert.Equal(CustomersSize, await ReadSizeAsync(tenantRepository, binaryId));
        Assert.NotNull(await GetEntityAsync(tenantRepository, entity.RtId));
    }

    [Fact]
    public async Task Delete_SessionDisposedWithoutCommit_KeepsBytes()
    {
        var tenantRepository = fixture.GetSystemContext().GetTenantRepository();
        var entity = await InsertCommittedAsync(tenantRepository);
        var binaryId = entity.Binary.BinaryId!.Value;

        using (var session = await tenantRepository.GetSessionAsync())
        {
            session.StartTransaction();
            await tenantRepository.DeleteOneRtEntityByRtIdAsync<RtBinaryEntity>(session, entity.RtId,
                DeleteOptions.Erase);
        }

        Assert.True(await BinaryExistsAsync(tenantRepository, binaryId));
    }

    [Fact]
    public async Task Delete_WithoutTransaction_RemovesBytesImmediately()
    {
        var tenantRepository = fixture.GetSystemContext().GetTenantRepository();
        var entity = await InsertCommittedAsync(tenantRepository);
        var binaryId = entity.Binary.BinaryId!.Value;

        using var session = await tenantRepository.GetSessionAsync();
        await tenantRepository.DeleteOneRtEntityByRtIdAsync<RtBinaryEntity>(session, entity.RtId, DeleteOptions.Erase);

        Assert.False(await BinaryExistsAsync(tenantRepository, binaryId));
    }

    [Fact]
    public async Task Replace_OldBytesRemainUntilCommit_ThenOnlyNewBytesRemain()
    {
        var tenantRepository = fixture.GetSystemContext().GetTenantRepository();
        var entity = await InsertCommittedAsync(tenantRepository);
        var oldBinaryId = entity.Binary.BinaryId!.Value;

        var replacement = ProductsEntity();
        using var session = await tenantRepository.GetSessionAsync();
        session.StartTransaction();
        await tenantRepository.ReplaceOneRtEntityByIdAsync(session, entity.RtId, replacement);

        Assert.Equal(CustomersSize, await ReadSizeAsync(tenantRepository, oldBinaryId));

        await session.CommitTransactionAsync();

        var stored = await GetEntityAsync(tenantRepository, entity.RtId);
        var newBinaryId = stored!.Binary.BinaryId!.Value;
        Assert.Equal(ProductsSize, await ReadSizeAsync(tenantRepository, newBinaryId));
        if (newBinaryId != oldBinaryId)
        {
            Assert.False(await BinaryExistsAsync(tenantRepository, oldBinaryId), "old bytes must be gone after commit");
        }
    }

    [Fact]
    public async Task Update_SameBinaryId_OldBytesRemainUntilCommit_ThenNewBytesUnderSameId()
    {
        var tenantRepository = fixture.GetSystemContext().GetTenantRepository();
        var entity = await InsertCommittedAsync(tenantRepository);
        var binaryId = entity.Binary.BinaryId!.Value;

        using var session = await tenantRepository.GetSessionAsync();
        session.StartTransaction();
        await tenantRepository.UpdateOneRtEntityByIdAsync(session, entity.RtId, new RtBinaryEntity
        {
            Binary = ProductsBinary(binaryId)
        });

        Assert.Equal(CustomersSize, await ReadSizeAsync(tenantRepository, binaryId));

        await session.CommitTransactionAsync();

        Assert.Equal(ProductsSize, await ReadSizeAsync(tenantRepository, binaryId));
        var stored = await GetEntityAsync(tenantRepository, entity.RtId);
        Assert.Equal(binaryId, stored!.Binary.BinaryId);
        Assert.Equal("Products.pdf", stored.Binary.Filename);

        // the entity delete must still find the replaced bytes
        using var deleteSession = await tenantRepository.GetSessionAsync();
        deleteSession.StartTransaction();
        await tenantRepository.DeleteOneRtEntityByRtIdAsync<RtBinaryEntity>(deleteSession, entity.RtId,
            DeleteOptions.Erase);
        await deleteSession.CommitTransactionAsync();
        Assert.False(await BinaryExistsAsync(tenantRepository, binaryId));
    }

    [Fact]
    public async Task Update_SameBinaryId_Rollback_KeepsOldBytes()
    {
        var tenantRepository = fixture.GetSystemContext().GetTenantRepository();
        var entity = await InsertCommittedAsync(tenantRepository);
        var binaryId = entity.Binary.BinaryId!.Value;

        using (var session = await tenantRepository.GetSessionAsync())
        {
            session.StartTransaction();
            await tenantRepository.UpdateOneRtEntityByIdAsync(session, entity.RtId, new RtBinaryEntity
            {
                Binary = ProductsBinary(binaryId)
            });
            await session.AbortTransactionAsync();
        }

        Assert.Equal(CustomersSize, await ReadSizeAsync(tenantRepository, binaryId));
    }

    [Fact]
    public async Task Replace_Rollback_KeepsOldBytes()
    {
        var tenantRepository = fixture.GetSystemContext().GetTenantRepository();
        var entity = await InsertCommittedAsync(tenantRepository);
        var binaryId = entity.Binary.BinaryId!.Value;

        using (var session = await tenantRepository.GetSessionAsync())
        {
            session.StartTransaction();
            await tenantRepository.ReplaceOneRtEntityByIdAsync(session, entity.RtId, ProductsEntity());
            await session.AbortTransactionAsync();
        }

        Assert.Equal(CustomersSize, await ReadSizeAsync(tenantRepository, binaryId));
        var stored = await GetEntityAsync(tenantRepository, entity.RtId);
        Assert.Equal("Customers.xlsx", stored!.Binary.Filename);
    }

    private static RtBinaryEntity ProductsEntity() => new()
    {
        DataCount = 7,
        Binary = ProductsBinary(null)
    };

    // A supplied BinaryId makes the engine replace the bytes under the same id (instead of uploading a new file).
    private static EntityBinaryInfo ProductsBinary(OctoObjectId? binaryId) => new()
    {
        BinaryId = binaryId,
        Filename = "Products.pdf",
        ContentType = "application/pdf",
        Stream = File.OpenRead("testData/largeBinaries/Products.pdf")
    };

    private static async Task<RtBinaryEntity> InsertCommittedAsync(ITenantRepository tenantRepository)
    {
        var entity = new RtBinaryEntity
        {
            RtId = OctoObjectId.GenerateNewId(),
            DataCount = 5,
            Binary = new EntityBinaryInfo
            {
                Filename = "Customers.xlsx",
                ContentType = "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet",
                Stream = File.OpenRead("testData/largeBinaries/Customers.xlsx")
            }
        };

        using var session = await tenantRepository.GetSessionAsync();
        session.StartTransaction();
        await tenantRepository.InsertOneRtEntityAsync(session, entity);
        await session.CommitTransactionAsync();
        Assert.NotNull(entity.Binary.BinaryId);
        return entity;
    }

    private static async Task<RtBinaryEntity?> GetEntityAsync(ITenantRepository tenantRepository, OctoObjectId rtId)
    {
        using var session = await tenantRepository.GetSessionAsync();
        session.StartTransaction();
        var r = await tenantRepository.GetRtEntityByRtIdAsync<RtBinaryEntity>(session, rtId);
        await session.CommitTransactionAsync();
        return r;
    }

    private static async Task<bool> BinaryExistsAsync(ITenantRepository tenantRepository, OctoObjectId binaryId)
    {
        using var session = await tenantRepository.GetSessionAsync();
        try
        {
            using var handler = await tenantRepository.DownloadLargeBinaryAsync(session, binaryId);
            return true;
        }
        catch (EntityNotFoundException)
        {
            return false;
        }
    }

    private static async Task<long> ReadSizeAsync(ITenantRepository tenantRepository, OctoObjectId binaryId)
    {
        using var session = await tenantRepository.GetSessionAsync();
        using var handler = await tenantRepository.DownloadLargeBinaryAsync(session, binaryId);
        using var ms = new MemoryStream();
        await handler.Stream.CopyToAsync(ms);
        return ms.Length;
    }
}
