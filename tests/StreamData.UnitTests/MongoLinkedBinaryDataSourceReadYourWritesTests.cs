using FakeItEasy;

using Meshmakers.Octo.ConstructionKit.Contracts;
using Meshmakers.Octo.Runtime.Contracts;
using Meshmakers.Octo.Runtime.Contracts.MongoDb;
using Meshmakers.Octo.Runtime.Engine.MongoDb;
using Meshmakers.Octo.Runtime.Contracts.Repositories;
using Meshmakers.Octo.Runtime.Engine.MongoDb.Repositories.MongoDb;
using Meshmakers.Octo.Runtime.Engine.MongoDb.Repositories.MongoDb.Generic;

using MongoDB.Bson;
using MongoDB.Driver;
using MongoDB.Driver.GridFS;

namespace Meshmakers.Octo.Runtime.Engine.UnitTests;

/// <summary>
/// AB#7256: a file that was written moments ago may not be visible on the secondary the default GridFS bucket reads
/// from. The data source must ask the primary before it reports the file as missing.
/// </summary>
public class MongoLinkedBinaryDataSourceReadYourWritesTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static (MongoLinkedBinaryDataSource Source, IGridFSBucket Secondary, IGridFSBucket Primary) Create()
    {
        var secondary = A.Fake<IGridFSBucket>();
        var primary = A.Fake<IGridFSBucket>();
        var repository = A.Fake<IRepositoryInternal>();
        A.CallTo(() => repository.GetGridFsBucket()).Returns(secondary);
        A.CallTo(() => repository.GetPrimaryGridFsBucket()).Returns(primary);
        var client = A.Fake<IRepositoryClient>();
        A.CallTo(() => client.GetRepository("db")).Returns(repository);
        return (new MongoLinkedBinaryDataSource(client, "db"), secondary, primary);
    }

    private static void FileMissing(IGridFSBucket bucket, ObjectId id)
    {
        A.CallTo(() => bucket.OpenDownloadStreamAsync(id, A<GridFSDownloadOptions>._, A<CancellationToken>._))
            .Throws(new GridFSFileNotFoundException(id));
    }

    [Fact]
    public async Task Download_FileMissingOnSecondary_IsReadFromPrimary()
    {
        var (source, secondary, primary) = Create();
        var id = ObjectId.GenerateNewId();
        FileMissing(secondary, id);
        var stream = A.Fake<GridFSDownloadStream<ObjectId>>();
        A.CallTo(() => primary.OpenDownloadStreamAsync(id, A<GridFSDownloadOptions>._, A<CancellationToken>._))
            .Returns(Task.FromResult(stream));

        var handler = await source.DownloadBinaryAsync(A.Fake<IOctoSession>(), id.ToOctoObjectId(), Ct);

        Assert.NotNull(handler);
        A.CallTo(() => primary.OpenDownloadStreamAsync(id, A<GridFSDownloadOptions>._, A<CancellationToken>._))
            .MustHaveHappenedOnceExactly();
    }

    [Fact]
    public async Task Download_FileFoundOnSecondary_DoesNotTouchThePrimary()
    {
        var (source, secondary, primary) = Create();
        var id = ObjectId.GenerateNewId();
        var stream = A.Fake<GridFSDownloadStream<ObjectId>>();
        A.CallTo(() => secondary.OpenDownloadStreamAsync(id, A<GridFSDownloadOptions>._, A<CancellationToken>._))
            .Returns(Task.FromResult(stream));

        await source.DownloadBinaryAsync(A.Fake<IOctoSession>(), id.ToOctoObjectId(), Ct);

        A.CallTo(primary).MustNotHaveHappened();
    }

    [Fact]
    public async Task Download_FileMissingEverywhere_ThrowsEntityNotFound()
    {
        var (source, secondary, primary) = Create();
        var id = ObjectId.GenerateNewId();
        FileMissing(secondary, id);
        FileMissing(primary, id);

        await Assert.ThrowsAsync<EntityNotFoundException>(() =>
            source.DownloadBinaryAsync(A.Fake<IOctoSession>(), id.ToOctoObjectId(), Ct));
    }
}
