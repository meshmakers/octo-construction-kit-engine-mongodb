using System.Globalization;

using Meshmakers.Octo.ConstructionKit.Contracts;
using Meshmakers.Octo.Runtime.Contracts;
using Meshmakers.Octo.Runtime.Contracts.MongoDb;
using Meshmakers.Octo.Runtime.Contracts.Repositories;
using Meshmakers.Octo.Runtime.Contracts.RepositoryEntities;
using Meshmakers.Octo.Runtime.Engine.MongoDb.Repositories.MongoDb.Generic;

using MongoDB.Bson;
using MongoDB.Driver;
using MongoDB.Driver.GridFS;

using BinaryInfo = Meshmakers.Octo.Runtime.Engine.MongoDb.Repositories.MongoDb.Generic.BinaryInfo;
using DeleteOptions = Meshmakers.Octo.Runtime.Contracts.Repositories.DeleteOptions;

namespace Meshmakers.Octo.Runtime.Engine.MongoDb.Repositories.MongoDb;

public class MongoLinkedBinaryDataSource : LinkedBinaryDataSource
{
    private readonly IGridFSBucket _bucket;

    public MongoLinkedBinaryDataSource(IRepositoryClient repositoryClient, string databaseName)
    {
        var repository = (IRepositoryInternal)repositoryClient.GetRepository(databaseName);
        _bucket = repository.GetGridFsBucket();
    }

    public override async Task DeleteAllFileSystemBinariesAsync(IOctoSession session, RtEntityId rtEntityId,
        CancellationToken cancellationToken = new())
    {
        var filter = Builders<GridFSFileInfo>.Filter.Eq("Metadata." + Constants.RtEntityId,
            rtEntityId.ToString(CultureInfo.InvariantCulture));
        var asyncCursor = await _bucket.FindAsync(filter, cancellationToken: cancellationToken);

        var ids = new List<ObjectId>();
        while (await asyncCursor.MoveNextAsync(cancellationToken))
        {
            ids.AddRange(asyncCursor.Current.Select(x => x.Id));
        }

        if (ids.Count == 0)
        {
            return;
        }

        // Inside a transaction the bytes must outlive a rollback: delete them only after the commit (AB#6248).
        if (TryGetActiveTransaction(session) is { } transaction)
        {
            transaction.RegisterTransactionCallbacks(async () =>
            {
                foreach (var id in ids)
                {
                    await DeleteIfExistsAsync(id, CancellationToken.None).ConfigureAwait(false);
                }
            });
            return;
        }

        foreach (var id in ids)
        {
            await _bucket.DeleteAsync(id, cancellationToken);
        }
    }

    public override async Task DeleteTemporaryLargeBinaryAsync(IOctoSession session, OctoObjectId largeBinaryId,
        CancellationToken cancellationToken = new())
    {
        await _bucket.DeleteAsync(largeBinaryId.ToObjectId(), cancellationToken);
    }

    public override async Task<IDownloadStreamHandler> DownloadBinaryAsync(IOctoSession session,
        OctoObjectId largeBinaryId,
        CancellationToken cancellationToken = new())
    {
        try
        {
            var gridFsDownloadStream =
                await _bucket.OpenDownloadStreamAsync(largeBinaryId.ToObjectId(), cancellationToken: cancellationToken);
            return new DownloadStreamHandler(gridFsDownloadStream);
        }
        catch (GridFSFileNotFoundException e)
        {
            throw EntityNotFoundException.IdNotFound(nameof(IDownloadStreamHandler), largeBinaryId.ToString(), e);
        }
    }

    public override async Task<IBinaryInfo?> GetFileSystemBinaryAsync(IOctoSession session, OctoObjectId largeBinaryId,
        CancellationToken cancellationToken = new())
    {
        var filter = Builders<GridFSFileInfo>.Filter.Eq("_id", largeBinaryId);
        var asyncCursor = await _bucket.FindAsync(filter, cancellationToken: cancellationToken);
        var gridFsFileInfo = await asyncCursor.FirstOrDefaultAsync(cancellationToken);
        return gridFsFileInfo == null ? null : new BinaryInfo(gridFsFileInfo);
    }

    public override async Task<IBinaryInfo?> GetTemporaryBinaryAsync(IOctoSession session, string fileName,
        CancellationToken cancellationToken = new())
    {
        var filter = Builders<GridFSFileInfo>.Filter.Eq("Filename", fileName);
        filter &= Builders<GridFSFileInfo>.Filter.Eq("Metadata." + Constants.BinaryType, (int)BinaryType.Temporary);
        var asyncCursor = await _bucket.FindAsync(filter, cancellationToken: cancellationToken);
        var gridFsFileInfo = await asyncCursor.FirstOrDefaultAsync(cancellationToken);
        return gridFsFileInfo == null ? null : new BinaryInfo(gridFsFileInfo);
    }

    public override async Task DeleteExpiredTemporaryLargeBinariesAsync(IOctoSession session, DateTime expiryDateTime,
        CancellationToken cancellationToken)
    {
        var filter = Builders<GridFSFileInfo>.Filter.Eq("Metadata." + Constants.BinaryType, (int)BinaryType.Temporary);
        filter &= Builders<GridFSFileInfo>.Filter.Lte("Metadata." + Constants.ExpiryDateTime, expiryDateTime);
        var asyncCursor = await _bucket.FindAsync(filter, cancellationToken: cancellationToken);

        while (await asyncCursor.MoveNextAsync(cancellationToken))
        {
            foreach (var gridFsFileInfo in asyncCursor.Current)
            {
                await _bucket.DeleteAsync(gridFsFileInfo.Id, cancellationToken);
            }
        }
    }

    public override async Task DeleteAllTemporaryLargeBinariesAsync(IOctoSession session,
        CancellationToken cancellationToken)
    {
        var filter = Builders<GridFSFileInfo>.Filter.Eq("Metadata." + Constants.BinaryType, (int)BinaryType.Temporary);
        filter &= Builders<GridFSFileInfo>.Filter.Ne("Metadata." + Constants.ExpiryDateTime, BsonNull.Value);
        var asyncCursor = await _bucket.FindAsync(filter, cancellationToken: cancellationToken);

        while (await asyncCursor.MoveNextAsync(cancellationToken))
        {
            foreach (var gridFsFileInfo in asyncCursor.Current)
            {
                await _bucket.DeleteAsync(gridFsFileInfo.Id, cancellationToken);
            }
        }
    }

    protected override async Task<OctoObjectId> UploadLargeBinaryAsync(IOctoSession session, string filename,
        string contentType, BinaryType binaryType,
        RtEntityId? rtEntityId, DateTime? expiryDateTime, Stream stream,
        CancellationToken cancellationToken = new())
    {
        var options = new GridFSUploadOptions
        {
            Metadata = new BsonDocument
            {
                { Constants.ContentType, contentType },
                { Constants.BinaryType, binaryType },
                { Constants.ExpiryDateTime, expiryDateTime },
            }
        };

        if (rtEntityId != null)
        {
            options.Metadata.Add(Constants.RtEntityId, rtEntityId.ToString());
        }

        var uploadedId = await _bucket.UploadFromStreamAsync(filename, stream, options, cancellationToken);

        if (binaryType == BinaryType.FileSystem && TryGetActiveTransaction(session) is { } transaction)
        {
            // The upload is not part of the Mongo transaction; remove the bytes again if it is rolled back.
            transaction.RegisterTransactionCallbacks(null,
                () => DeleteIfExistsAsync(uploadedId, CancellationToken.None));
        }

        return uploadedId.ToOctoObjectId();
    }

    protected override async Task<OctoObjectId> ReplaceLargeBinaryAsync(IOctoSession session, string filename,
        string contentType, BinaryType binaryType,
        OctoObjectId? binaryId, Stream stream, CancellationToken cancellationToken = new())
    {
        if (binaryId != null && binaryType == BinaryType.FileSystem &&
            TryGetActiveTransaction(session) is { } transaction)
        {
            await StageReplacementAsync(transaction, filename, contentType, binaryType, binaryId.Value, stream,
                cancellationToken);
            return binaryId.Value;
        }

        BsonDocument meta;
        if (binaryId == null)
        {
            var filter = Builders<GridFSFileInfo>.Filter.Eq("Filename", filename);
            filter &= Builders<GridFSFileInfo>.Filter.Eq("Metadata." + Constants.BinaryType, (int)binaryType);
            var asyncCursor = await _bucket.FindAsync(filter, cancellationToken: cancellationToken);
            var gridFsFileInfo = await asyncCursor.FirstOrDefaultAsync(cancellationToken);
            if (gridFsFileInfo != null)
            {
                await _bucket.DeleteAsync(gridFsFileInfo.Id, cancellationToken);
                meta = gridFsFileInfo.Metadata;
                binaryId = gridFsFileInfo.Id.ToOctoObjectId();
            }
            else
            {
                binaryId = OctoObjectId.GenerateNewId();
                meta = new BsonDocument();
            }
        }
        else
        {
            meta = new BsonDocument();
        }

        meta[Constants.ContentType] = contentType;
        meta[Constants.BinaryType] = binaryType;

        var options = new GridFSUploadOptions { Metadata = meta };

        await _bucket.DeleteAsync(binaryId.Value.ToObjectId(), cancellationToken);
        await _bucket.UploadFromStreamAsync(binaryId.Value.ToObjectId(), filename, stream, options, cancellationToken);

        return binaryId.Value;
    }

    private static IOctoSessionInternal? TryGetActiveTransaction(IOctoSession session)
    {
        return session is IOctoSessionInternal { IsTransactionActive: true } internalSession ? internalSession : null;
    }

    private async Task DeleteIfExistsAsync(ObjectId id, CancellationToken cancellationToken)
    {
        try
        {
            await _bucket.DeleteAsync(id, cancellationToken);
        }
        catch (GridFSFileNotFoundException)
        {
            // already gone
        }
    }

    private IMongoCollection<BsonDocument> GetBucketCollection(string suffix)
    {
        return _bucket.Database.GetCollection<BsonDocument>(_bucket.Options.BucketName + "." + suffix)
            .WithReadPreference(ReadPreference.Primary)
            .WithWriteConcern(WriteConcern.WMajority);
    }

    /// <summary>
    ///     Transactional replace: the new bytes are uploaded under a staging id; the old bytes stay readable until
    ///     the transaction commits. After the commit the staging file is re-keyed to the original id (metadata
    ///     only, no byte copy). On rollback the staging file is removed and nothing changes.
    /// </summary>
    private async Task StageReplacementAsync(IOctoSessionInternal transaction, string filename, string contentType,
        BinaryType binaryType, OctoObjectId binaryId, Stream stream, CancellationToken cancellationToken)
    {
        var originalId = binaryId.ToObjectId();
        var stagingId = ObjectId.GenerateNewId();

        var oldFile = await GetBucketCollection("files")
            .Find(Builders<BsonDocument>.Filter.Eq("_id", originalId))
            .FirstOrDefaultAsync(cancellationToken);

        // Keep the metadata of the existing file (e.g. the owning entity) so that the entity delete still finds it.
        var meta = oldFile != null && oldFile.TryGetValue("metadata", out var oldMeta) && oldMeta.IsBsonDocument
            ? oldMeta.AsBsonDocument.DeepClone().AsBsonDocument
            : new BsonDocument();
        meta[Constants.ContentType] = contentType;
        meta[Constants.BinaryType] = binaryType;

        await _bucket.UploadFromStreamAsync(stagingId, filename, stream, new GridFSUploadOptions { Metadata = meta },
            cancellationToken);

        transaction.RegisterTransactionCallbacks(
            () => SwapStagedAsync(originalId, stagingId),
            () => DeleteIfExistsAsync(stagingId, CancellationToken.None));
    }

    private async Task SwapStagedAsync(ObjectId originalId, ObjectId stagingId)
    {
        var files = GetBucketCollection("files");
        var chunks = GetBucketCollection("chunks");

        var stagedFile = await files.Find(Builders<BsonDocument>.Filter.Eq("_id", stagingId))
            .FirstOrDefaultAsync();
        if (stagedFile == null)
        {
            throw new InvalidOperationException($"Staged GridFS file '{stagingId}' not found.");
        }

        await DeleteIfExistsAsync(originalId, CancellationToken.None).ConfigureAwait(false);

        stagedFile["_id"] = originalId;
        await files.InsertOneAsync(stagedFile).ConfigureAwait(false);
        await chunks.UpdateManyAsync(Builders<BsonDocument>.Filter.Eq("files_id", stagingId),
            Builders<BsonDocument>.Update.Set("files_id", originalId)).ConfigureAwait(false);
        await files.DeleteOneAsync(Builders<BsonDocument>.Filter.Eq("_id", stagingId)).ConfigureAwait(false);
    }
}
