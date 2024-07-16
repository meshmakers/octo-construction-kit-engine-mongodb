using Meshmakers.Octo.ConstructionKit.Contracts;

namespace Meshmakers.Octo.Runtime.Contracts.MongoDb.Repositories;

public interface IDownloadInfo
{
    public string ContentType { get; }
    public OctoObjectId BinaryId { get; }
    public string Filename { get; }
    public DateTime UploadDateTime { get; }
    public long Length { get; }
}