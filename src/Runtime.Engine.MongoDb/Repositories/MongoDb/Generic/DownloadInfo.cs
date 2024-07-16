using Meshmakers.Octo.ConstructionKit.Contracts;
using Meshmakers.Octo.Runtime.Contracts.MongoDb.Repositories;
using MongoDB.Driver.GridFS;

namespace Meshmakers.Octo.Runtime.Engine.MongoDb.Repositories.MongoDb.Generic;

internal class DownloadInfo(GridFSFileInfo fsFileInfo) : IDownloadInfo
{
    public string ContentType => fsFileInfo.Metadata.GetValue(Constants.ContentType).AsString;
    public OctoObjectId BinaryId => fsFileInfo.Id.ToOctoObjectId();
    public string Filename => fsFileInfo.Filename;
    public DateTime UploadDateTime => fsFileInfo.UploadDateTime;
    public long Length => fsFileInfo.Length;
}