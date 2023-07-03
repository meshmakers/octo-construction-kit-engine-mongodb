using System;
using Meshmakers.Octo.Common.Shared;

namespace Meshmakers.Octo.SystematizedData.Persistence;

public interface IDownloadInfo
{
    public string ContentType { get; }
    public OctoObjectId BinaryId { get; }
    public string Filename { get; }
    public DateTime UploadDateTime { get; }
    public long Length { get; }
}
