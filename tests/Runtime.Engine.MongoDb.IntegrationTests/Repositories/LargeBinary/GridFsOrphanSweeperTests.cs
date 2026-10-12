using Meshmakers.Octo.ConstructionKit.Contracts;
using Meshmakers.Octo.Runtime.Contracts;
using Meshmakers.Octo.Runtime.Contracts.MongoDb;
using Meshmakers.Octo.Runtime.Contracts.MongoDb.Configuration;
using Meshmakers.Octo.Runtime.Contracts.MongoDb.Repositories;
using Meshmakers.Octo.Runtime.Contracts.Repositories;
using Meshmakers.Octo.Runtime.Contracts.RepositoryEntities;
using Meshmakers.Octo.Runtime.Engine.MongoDb.IntegrationTests.Collections;
using Meshmakers.Octo.Runtime.Engine.MongoDb.IntegrationTests.Fixtures;
using Meshmakers.Octo.Runtime.Engine.MongoDb.Repositories.MongoDb;
using Meshmakers.Octo.Runtime.Engine.MongoDb.Repositories.MongoDb.Generic;

using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

using MongoDB.Bson;
using MongoDB.Driver;
using MongoDB.Driver.GridFS;

using TestCkModel.Generated.Test.v1;

using Xunit;

namespace Meshmakers.Octo.Runtime.Engine.MongoDb.IntegrationTests.Repositories.LargeBinary;

/// <summary>
///     AB#6541: orphaned GridFS bytes are found and removed. The collection is shared with the other linked-binary
///     tests, which run one after the other.
/// </summary>
[Collection(ImportTestCkModelCollection.Name)]
public class GridFsOrphanSweeperTests(ImportTestCkModelFixture fixture)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static GridFsOrphanSweepOptions Remove => new() { Remove = true, GracePeriod = GridFsOrphanSweepOptions.MinimumGracePeriod };

    [Fact]
    public async Task GracePeriodBelowTenTransactionLifetimes_IsRefused()
    {
        var sweeper = CreateSweeper();
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => sweeper.SweepAsync(
            new GridFsOrphanSweepOptions { GracePeriod = TimeSpan.FromSeconds(60) }, Ct));
        Assert.Equal(TimeSpan.FromHours(24), new GridFsOrphanSweepOptions().GracePeriod);
        Assert.False(new GridFsOrphanSweepOptions().Remove);
    }

    [Fact]
    public async Task ReferencedBytes_Survive_AndOldBytesAfterCrashAreRemoved()
    {
        var tenantRepository = fixture.GetSystemContext().GetTenantRepository();
        var entity = await InsertCommittedAsync(tenantRepository);
        var oldId = entity.Binary.BinaryId!.Value;

        // crash between commit and after-commit delete: the old bytes stay behind
        var update = new RtBinaryEntity { Binary = ProductsBinary(oldId) };
        var session = await tenantRepository.GetSessionAsync();
        session.StartTransaction();
        await tenantRepository.UpdateOneRtEntityByIdAsync(session, entity.RtId, update);
        await ((IOctoSessionInternal)session).SessionHandle.CommitTransactionAsync(Ct);
        var newId = update.Binary.BinaryId!.Value;
        Assert.NotEqual(oldId, newId);

        await BackdateAsync(oldId.ToObjectId(), newId.ToObjectId());

        var result = await CreateSweeper().SweepAsync(Remove, Ct);

        Assert.True(result.OrphanFiles >= 1);
        Assert.False(await BinaryExistsAsync(tenantRepository, oldId), "orphaned old bytes must be removed");
        Assert.True(await BinaryExistsAsync(tenantRepository, newId), "referenced bytes must survive");
        Assert.Equal(0, await ChunkCountAsync(oldId.ToObjectId()));
        Assert.True(await ChunkCountAsync(newId.ToObjectId()) > 0);
    }

    [Fact]
    public async Task StagedBytesAfterCrashBeforeCommit_AndBytesWithoutOwner_AndBytesOfDeletedEntity_AreRemoved()
    {
        var tenantRepository = fixture.GetSystemContext().GetTenantRepository();
        var bucket = GetBucket();

        var withoutOwner = await UploadAsync(bucket, null);
        var ofDeletedEntity = await UploadAsync(bucket, OctoObjectId.GenerateNewId().ToString());
        // staged bytes of a transaction that never committed: uploaded, no entity references them
        var staged = await UploadAsync(bucket, null);
        await BackdateAsync(withoutOwner, ofDeletedEntity, staged);

        var dryRun = await CreateSweeper().SweepAsync(
            new GridFsOrphanSweepOptions { GracePeriod = GridFsOrphanSweepOptions.MinimumGracePeriod }, Ct);
        Assert.False(dryRun.Removed);
        Assert.True(dryRun.OrphanFiles >= 3);
        Assert.True(dryRun.OrphanBytes > 0);
        Assert.True(dryRun.OrphanChunks >= 3);
        Assert.Equal(0, dryRun.RemovedFiles);
        Assert.True(await BinaryExistsAsync(tenantRepository, withoutOwner.ToOctoObjectId()), "dry run deletes nothing");

        var result = await CreateSweeper().SweepAsync(Remove, Ct);

        Assert.True(result.RemovedFiles >= 3);
        foreach (var id in new[] { withoutOwner, ofDeletedEntity, staged })
        {
            Assert.False(await BinaryExistsAsync(tenantRepository, id.ToOctoObjectId()));
            Assert.Equal(0, await ChunkCountAsync(id));
        }

        // second run removes nothing
        var again = await CreateSweeper().SweepAsync(Remove, Ct);
        Assert.Equal(0, again.OrphanFiles);
        Assert.Equal(0, again.RemovedFiles);
    }

    [Fact]
    public async Task FileYoungerThanGracePeriod_AndTemporaryBinary_Survive()
    {
        var tenantRepository = fixture.GetSystemContext().GetTenantRepository();
        var bucket = GetBucket();
        var young = await UploadAsync(bucket, null); // unreferenced but not backdated
        var temporary = await UploadAsync(bucket, null, BinaryType.Temporary);
        await BackdateAsync(temporary); // old, unreferenced, but not a FileSystem binary

        var result = await CreateSweeper().SweepAsync(Remove, Ct);

        Assert.True(result.YoungFiles >= 1);
        Assert.True(await BinaryExistsAsync(tenantRepository, young.ToOctoObjectId()));
        Assert.True(await GridFsFileExistsAsync(temporary));
    }

    [Fact]
    public async Task StagedBytesOfInFlightTransaction_Survive()
    {
        var tenantRepository = fixture.GetSystemContext().GetTenantRepository();
        var entity = await InsertCommittedAsync(tenantRepository);
        var oldId = entity.Binary.BinaryId!.Value;

        using var session = await tenantRepository.GetSessionAsync();
        session.StartTransaction();
        var update = new RtBinaryEntity { Binary = ProductsBinary(oldId) };
        await tenantRepository.UpdateOneRtEntityByIdAsync(session, entity.RtId, update);
        var stagedId = update.Binary.BinaryId!.Value;

        // the transaction is open: new bytes are staged, not yet referenced by any committed entity
        await CreateSweeper().SweepAsync(Remove, Ct);
        Assert.True(await GridFsFileExistsAsync(stagedId.ToObjectId()), "staged bytes of an open transaction survive");

        await session.CommitTransactionAsync();
        Assert.True(await BinaryExistsAsync(tenantRepository, stagedId));
        Assert.False(await BinaryExistsAsync(tenantRepository, oldId), "the after-commit delete still ran");
    }

    private GridFsOrphanSweeper CreateSweeper()
    {
        var (client, dbName) = GetClient();
        return new GridFsOrphanSweeper(client, dbName, NullLogger<GridFsOrphanSweeper>.Instance);
    }

    private (IRepositoryClient client, string dbName) GetClient()
    {
        var userAccess = fixture.GetService<IUserRepositoryAccess>();
        var config = fixture.GetService<IOptions<OctoSystemConfiguration>>().Value;
        var dbName = config.SystemDatabaseName.ToLower();
        return (userAccess.GetRepositoryClient(dbName), dbName);
    }

    private IMongoDatabase Database
    {
        get
        {
            var (client, dbName) = GetClient();
            return ((MongoRepository)client.GetRepository(dbName)).Database;
        }
    }

    private IGridFSBucket GetBucket()
    {
        var (client, dbName) = GetClient();
        return ((IRepositoryInternal)client.GetRepository(dbName)).GetGridFsBucket();
    }

    private static async Task<ObjectId> UploadAsync(IGridFSBucket bucket, string? ownerRtEntityId,
        BinaryType binaryType = BinaryType.FileSystem)
    {
        var metadata = new BsonDocument
        {
            { Constants.ContentType, "application/octet-stream" },
            { Constants.BinaryType, (int)binaryType },
            { Constants.ExpiryDateTime, BsonNull.Value }
        };
        if (ownerRtEntityId != null)
        {
            metadata.Add(Constants.RtEntityId, ownerRtEntityId);
        }

        await using var stream = new MemoryStream(new byte[3000]);
        return await bucket.UploadFromStreamAsync("orphan.bin", stream,
            new GridFSUploadOptions { Metadata = metadata, ChunkSizeBytes = 1024 }, Ct);
    }

    private async Task BackdateAsync(params ObjectId[] ids)
    {
        var files = Database.GetCollection<BsonDocument>("fs.files");
        await files.UpdateManyAsync(Builders<BsonDocument>.Filter.In("_id", ids),
            Builders<BsonDocument>.Update.Set("uploadDate", DateTime.UtcNow.AddHours(-2)), cancellationToken: Ct);
    }

    private async Task<bool> GridFsFileExistsAsync(ObjectId id)
    {
        var files = Database.GetCollection<BsonDocument>("fs.files");
        return await files.CountDocumentsAsync(Builders<BsonDocument>.Filter.Eq("_id", id), cancellationToken: Ct) > 0;
    }

    private async Task<long> ChunkCountAsync(ObjectId id)
    {
        var chunks = Database.GetCollection<BsonDocument>("fs.chunks");
        return await chunks.CountDocumentsAsync(Builders<BsonDocument>.Filter.Eq("files_id", id), cancellationToken: Ct);
    }

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
}
