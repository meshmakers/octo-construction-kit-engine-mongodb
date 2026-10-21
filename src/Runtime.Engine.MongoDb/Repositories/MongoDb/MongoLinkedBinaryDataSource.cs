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
    private readonly IGridFSBucket _primaryBucket;

    public MongoLinkedBinaryDataSource(IRepositoryClient repositoryClient, string databaseName)
    {
        var repository = (IRepositoryInternal)repositoryClient.GetRepository(databaseName);
        _bucket = repository.GetGridFsBucket();
        _primaryBucket = repository.GetPrimaryGridFsBucket();
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
        var id = largeBinaryId.ToObjectId();
        try
        {
            return new DownloadStreamHandler(
                await _bucket.OpenDownloadStreamAsync(id, cancellationToken: cancellationToken));
        }
        catch (GridFSFileNotFoundException)
        {
            // AB#7256: the default bucket reads from a secondary, which can lag behind a write that another process
            // (e.g. the asset repository caching a CK model for an import job) has just made. Ask the primary before
            // reporting the file as missing; the primary is only hit on a miss, so regular reads stay on secondaries.
            try
            {
                return new DownloadStreamHandler(
                    await _primaryBucket.OpenDownloadStreamAsync(id, cancellationToken: cancellationToken));
            }
            catch (GridFSFileNotFoundException e)
            {
                throw EntityNotFoundException.IdNotFound(nameof(IDownloadStreamHandler), largeBinaryId.ToString(), e);
            }
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
        if (gridFsFileInfo == null)
        {
            // AB#7256: a cache file written moments ago may not have reached the secondary yet; ask the primary.
            asyncCursor = await _primaryBucket.FindAsync(filter, cancellationToken: cancellationToken);
            gridFsFileInfo = await asyncCursor.FirstOrDefaultAsync(cancellationToken);
        }

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
        string contentType, BinaryType binaryType, RtEntityId? rtEntityId,
        OctoObjectId? binaryId, Stream stream, CancellationToken cancellationToken = new())
    {
        if (binaryId != null && binaryType == BinaryType.FileSystem && rtEntityId != null)
        {
            // AB#6540: a BinaryId supplied by the caller must belong to the entity that is being updated.
            var existing = await FindFileAsync(binaryId.Value.ToObjectId(), cancellationToken);
            EnsureOwnedBy(existing, rtEntityId.Value, binaryId.Value);

            if (TryGetActiveTransaction(session) is { } transaction)
            {
                return await ReplaceInTransactionAsync(transaction, filename, contentType, binaryType,
                    rtEntityId.Value, existing, binaryId.Value, stream, cancellationToken);
            }
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
            if (rtEntityId != null)
            {
                meta[Constants.RtEntityId] = rtEntityId.Value.ToString(CultureInfo.InvariantCulture);
            }
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

    private async Task<GridFSFileInfo?> FindFileAsync(ObjectId id, CancellationToken cancellationToken)
    {
        var cursor = await _bucket.FindAsync(Builders<GridFSFileInfo>.Filter.Eq("_id", id),
            cancellationToken: cancellationToken);
        return await cursor.FirstOrDefaultAsync(cancellationToken);
    }

    /// <summary>
    ///     AB#6540: a file that exists and names another owner is refused (reported as not found, so that a forged
    ///     id reveals nothing). A missing file (already replaced by another session) and a file without owner
    ///     metadata (written before the owner was recorded) are accepted.
    /// </summary>
    private static void EnsureOwnedBy(GridFSFileInfo? file, RtEntityId rtEntityId, OctoObjectId binaryId)
    {
        if (file?.Metadata != null &&
            file.Metadata.TryGetValue(Constants.RtEntityId, out var owner) && owner.IsString &&
            owner.AsString != rtEntityId.ToString(CultureInfo.InvariantCulture))
        {
            throw EntityNotFoundException.IdNotFound(nameof(IBinaryInfo), binaryId.ToString());
        }
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

    /// <summary>
    ///     Transactional replace (AB#6248): the new bytes are uploaded under a NEW id, which is returned so that the
    ///     caller writes it into the entity inside the transaction. The old bytes stay readable until the commit and are
    ///     deleted only after it; on rollback the new bytes are deleted and nothing changes. A crash between the commit
    ///     and the delete leaves orphaned old bytes, never missing ones: the committed entity always references the new
    ///     bytes, which were fully uploaded before the commit.
    /// </summary>
    private async Task<OctoObjectId> ReplaceInTransactionAsync(IOctoSessionInternal transaction, string filename,
        string contentType, BinaryType binaryType, RtEntityId rtEntityId, GridFSFileInfo? oldFile,
        OctoObjectId binaryId, Stream stream, CancellationToken cancellationToken)
    {
        var oldId = binaryId.ToObjectId();
        var newId = ObjectId.GenerateNewId();

        // Keep the metadata of the existing file so that the entity delete still finds it. The owning entity is always
        // set from the caller: a stale BinaryId (the file was already replaced by another session) would otherwise
        // produce a new file without owner metadata that no entity delete ever finds, i.e. a permanent orphan.
        var meta = oldFile?.Metadata?.DeepClone().AsBsonDocument ?? new BsonDocument();
        meta[Constants.ContentType] = contentType;
        meta[Constants.BinaryType] = binaryType;
        meta[Constants.RtEntityId] = rtEntityId.ToString(CultureInfo.InvariantCulture);

        try
        {
            await _bucket.UploadFromStreamAsync(newId, filename, stream, new GridFSUploadOptions { Metadata = meta },
                cancellationToken);
        }
        catch
        {
            // A failed upload may leave partial chunks behind. The old bytes are untouched and still referenced.
            await DeleteIfExistsAsync(newId, CancellationToken.None).ConfigureAwait(false);
            throw;
        }

        // Only now - the new bytes are complete - may the old bytes be scheduled for deletion after the commit.
        transaction.RegisterTransactionCallbacks(
            () => DeleteIfExistsAsync(oldId, CancellationToken.None),
            () => DeleteIfExistsAsync(newId, CancellationToken.None));

        return newId.ToOctoObjectId();
    }
}
