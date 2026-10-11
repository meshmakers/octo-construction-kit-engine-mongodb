using Meshmakers.Octo.ConstructionKit.Contracts;
using Meshmakers.Octo.Runtime.Contracts;
using Meshmakers.Octo.Runtime.Contracts.MongoDb;
using Meshmakers.Octo.Runtime.Contracts.MongoDb.Repositories;
using Meshmakers.Octo.Runtime.Contracts.Repositories;
using Meshmakers.Octo.Runtime.Contracts.RepositoryEntities;
using Meshmakers.Octo.Runtime.Engine.MongoDb.Repositories.MongoDb;
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
    public async Task Update_WithBinaryId_NewBytesGetNewId_OldBytesDeletedOnlyAfterCommit()
    {
        var tenantRepository = fixture.GetSystemContext().GetTenantRepository();
        var entity = await InsertCommittedAsync(tenantRepository);
        var oldBinaryId = entity.Binary.BinaryId!.Value;

        var update = new RtBinaryEntity { Binary = ProductsBinary(oldBinaryId) };
        using var session = await tenantRepository.GetSessionAsync();
        session.StartTransaction();
        await tenantRepository.UpdateOneRtEntityByIdAsync(session, entity.RtId, update);

        // The id that is written into the entity inside the transaction is the staging id.
        var newBinaryId = update.Binary.BinaryId!.Value;
        Assert.NotEqual(oldBinaryId, newBinaryId);
        Assert.Equal(CustomersSize, await ReadSizeAsync(tenantRepository, oldBinaryId));
        Assert.Equal(ProductsSize, await ReadSizeAsync(tenantRepository, newBinaryId));

        await session.CommitTransactionAsync();

        var stored = await GetEntityAsync(tenantRepository, entity.RtId);
        Assert.Equal(newBinaryId, stored!.Binary.BinaryId);
        Assert.Equal("Products.pdf", stored.Binary.Filename);
        Assert.Equal(ProductsSize, await ReadSizeAsync(tenantRepository, newBinaryId));
        Assert.False(await BinaryExistsAsync(tenantRepository, oldBinaryId), "old bytes must be gone after commit");

        // the entity delete must still find the replaced bytes (owner metadata is carried over)
        using var deleteSession = await tenantRepository.GetSessionAsync();
        deleteSession.StartTransaction();
        await tenantRepository.DeleteOneRtEntityByRtIdAsync<RtBinaryEntity>(deleteSession, entity.RtId,
            DeleteOptions.Erase);
        await deleteSession.CommitTransactionAsync();
        Assert.False(await BinaryExistsAsync(tenantRepository, newBinaryId));
    }

    [Fact]
    public async Task Update_WithBinaryId_Rollback_KeepsOldBytesAndRemovesStagedBytes()
    {
        var tenantRepository = fixture.GetSystemContext().GetTenantRepository();
        var entity = await InsertCommittedAsync(tenantRepository);
        var oldBinaryId = entity.Binary.BinaryId!.Value;

        var update = new RtBinaryEntity { Binary = ProductsBinary(oldBinaryId) };
        using (var session = await tenantRepository.GetSessionAsync())
        {
            session.StartTransaction();
            await tenantRepository.UpdateOneRtEntityByIdAsync(session, entity.RtId, update);
            await session.AbortTransactionAsync();
        }

        Assert.Equal(CustomersSize, await ReadSizeAsync(tenantRepository, oldBinaryId));
        Assert.False(await BinaryExistsAsync(tenantRepository, update.Binary.BinaryId!.Value),
            "staged bytes must be removed on rollback");
        var stored = await GetEntityAsync(tenantRepository, entity.RtId);
        Assert.Equal(oldBinaryId, stored!.Binary.BinaryId);
        Assert.Equal("Customers.xlsx", stored.Binary.Filename);
    }

    [Fact]
    public async Task Update_WithBinaryId_CrashBetweenCommitAndDelete_LeavesOrphanButNeverMissingBytes()
    {
        var tenantRepository = fixture.GetSystemContext().GetTenantRepository();
        var entity = await InsertCommittedAsync(tenantRepository);
        var oldBinaryId = entity.Binary.BinaryId!.Value;

        var update = new RtBinaryEntity { Binary = ProductsBinary(oldBinaryId) };
        using (var session = await tenantRepository.GetSessionAsync())
        {
            session.StartTransaction();
            await tenantRepository.UpdateOneRtEntityByIdAsync(session, entity.RtId, update);

            // Fault injection: the Mongo transaction commits, but the process dies before the after-commit
            // callbacks run (the session never executes OctoSession.CommitTransactionAsync's cleanup).
            await ((IOctoSessionInternal)session).SessionHandle.CommitTransactionAsync(TestContext.Current.CancellationToken);
            // Disposal of a session whose transaction is already committed must not run rollback cleanup.
        }

        var newBinaryId = update.Binary.BinaryId!.Value;
        var stored = await GetEntityAsync(tenantRepository, entity.RtId);

        // never missing: the committed entity resolves to existing, complete bytes ...
        Assert.Equal(newBinaryId, stored!.Binary.BinaryId);
        Assert.Equal(ProductsSize, await ReadSizeAsync(tenantRepository, newBinaryId));
        // ... orphaned: the old bytes were not deleted (documented, removable by the orphan cleanup).
        Assert.True(await BinaryExistsAsync(tenantRepository, oldBinaryId));
    }

    [Fact]
    public async Task Update_WithBinaryId_FailingUpload_KeepsOldBytesReferenced()
    {
        var tenantRepository = fixture.GetSystemContext().GetTenantRepository();
        var entity = await InsertCommittedAsync(tenantRepository);
        var oldBinaryId = entity.Binary.BinaryId!.Value;

        var update = new RtBinaryEntity
        {
            Binary = new EntityBinaryInfo
            {
                BinaryId = oldBinaryId,
                Filename = "Broken.bin",
                ContentType = "application/octet-stream",
                Stream = new ThrowingStream()
            }
        };
        using (var session = await tenantRepository.GetSessionAsync())
        {
            session.StartTransaction();
            await Assert.ThrowsAnyAsync<Exception>(() =>
                tenantRepository.UpdateOneRtEntityByIdAsync(session, entity.RtId, update));
            // even if the caller committed anyway, the old bytes must not have been scheduled for deletion
            await session.CommitTransactionAsync();
        }

        Assert.Equal(CustomersSize, await ReadSizeAsync(tenantRepository, oldBinaryId));
    }

    private sealed class ThrowingStream : MemoryStream
    {
        public override int Read(byte[] buffer, int offset, int count) => throw new IOException("injected");

        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
            => throw new IOException("injected");

        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
            => throw new IOException("injected");
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

    // A supplied BinaryId makes the engine replace the bytes: the data source returns the id of the new bytes (AB#6248).
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
