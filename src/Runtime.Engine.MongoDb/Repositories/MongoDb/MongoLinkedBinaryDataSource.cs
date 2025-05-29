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
        var filter = Builders<GridFSFileInfo>.Filter.Eq("Metadata." + Constants.RtEntityId, rtEntityId.ToString(CultureInfo.InvariantCulture));
        var asyncCursor = await _bucket.FindAsync(filter, cancellationToken: cancellationToken);

        while (await asyncCursor.MoveNextAsync(cancellationToken))
        {
            foreach (var gridFsFileInfo in asyncCursor.Current)
            {
                await _bucket.DeleteAsync(gridFsFileInfo.Id, cancellationToken);
            }
        }
    }

    public override async Task DeleteTemporaryLargeBinaryAsync(IOctoSession session, OctoObjectId largeBinaryId,
        CancellationToken cancellationToken = new())
    {
        await _bucket.DeleteAsync(largeBinaryId.ToObjectId(), cancellationToken);
    }

    public override async Task<IDownloadStreamHandler> DownloadBinaryAsync(IOctoSession session, OctoObjectId largeBinaryId,
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

    protected override async Task<OctoObjectId> UploadLargeBinaryAsync(IOctoSession session, string filename, string contentType, BinaryType binaryType,
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

        return (await _bucket.UploadFromStreamAsync(filename, stream, options, cancellationToken)).ToOctoObjectId();
    }

    protected override async Task<OctoObjectId> ReplaceLargeBinaryAsync(IOctoSession session, string filename, string contentType, BinaryType binaryType,
        OctoObjectId? binaryId, Stream stream, CancellationToken cancellationToken = new())
    {
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

        var options = new GridFSUploadOptions
        {
            Metadata = meta
        };

        await _bucket.DeleteAsync(binaryId.Value.ToObjectId(), cancellationToken);
        await _bucket.UploadFromStreamAsync(binaryId.Value.ToObjectId(), filename, stream, options, cancellationToken);

        return binaryId.Value;
    }
}
