using Meshmakers.Octo.ConstructionKit.Contracts;
using Meshmakers.Octo.Runtime.Contracts.MongoDb.Repositories;
using MongoDB.Bson;
using MongoDB.Driver.GridFS;

namespace Meshmakers.Octo.Runtime.Engine.MongoDb.Repositories.MongoDb.Generic;

internal class DownloadStreamHandler(GridFSDownloadStream<ObjectId> stream) : IDownloadStreamHandler
{
    public void Dispose()
    {
        stream.Dispose();
    }

    public OctoObjectId Id => stream.FileInfo.Id.ToOctoObjectId();
    public string ContentType => stream.FileInfo.Metadata.GetValue(Constants.ContentType).AsBsonValue.AsString;
    public DateTime UploadDateTime => stream.FileInfo.UploadDateTime;
    public Stream Stream => stream;
    public string Filename => stream.FileInfo.Filename;

    public void Close(CancellationToken cancellationToken)
    {
        stream.Close(cancellationToken);
    }

    public async Task CloseAsync(CancellationToken cancellationToken = default)
    {
        await stream.CloseAsync(cancellationToken);
    }
}