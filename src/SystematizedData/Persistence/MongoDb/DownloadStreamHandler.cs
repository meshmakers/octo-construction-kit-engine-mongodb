using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Meshmakers.Octo.Common.Shared;
using MongoDB.Bson;
using MongoDB.Driver.GridFS;

namespace Meshmakers.Octo.Backend.Persistence.MongoDb;

internal class DownloadStreamHandler : IDownloadStreamHandler
{
    private readonly GridFSDownloadStream<ObjectId> _stream;

    public DownloadStreamHandler(GridFSDownloadStream<ObjectId> stream)
    {
        _stream = stream;
    }

    public void Dispose()
    {
        _stream.Dispose();
    }

    public OctoObjectId Id => _stream.FileInfo.Id.ToOctoObjectId();
    public string ContentType => _stream.FileInfo.Metadata.GetValue(Constants.ContentType).AsBsonValue.AsString;
    public DateTime UploadDateTime => _stream.FileInfo.UploadDateTime;
    public Stream Stream => _stream;
    public string Filename => _stream.FileInfo.Filename;

    public void Close(CancellationToken cancellationToken)
    {
        _stream.Close(cancellationToken);
    }

    public async Task CloseAsync(CancellationToken cancellationToken = default)
    {
        await _stream.CloseAsync(cancellationToken);
    }
}
