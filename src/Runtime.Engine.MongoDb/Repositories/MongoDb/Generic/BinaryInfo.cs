using Meshmakers.Octo.ConstructionKit.Contracts;
using Meshmakers.Octo.Runtime.Contracts.RepositoryEntities;

using MongoDB.Driver.GridFS;

namespace Meshmakers.Octo.Runtime.Engine.MongoDb.Repositories.MongoDb.Generic;

internal class BinaryInfo(GridFSFileInfo fsFileInfo) : IBinaryInfo
{
    public string ContentType => fsFileInfo.Metadata.GetValue(Constants.ContentType).AsString;
    public OctoObjectId BinaryId => fsFileInfo.Id.ToOctoObjectId();
    public string Filename => fsFileInfo.Filename;
    public DateTime UploadDateTime => fsFileInfo.UploadDateTime;
    public DateTime? ExpiryDateTime => fsFileInfo.Metadata[Constants.ExpiryDateTime].AsBsonValue.ToUniversalTime();
    public BinaryType BinaryType => (BinaryType)fsFileInfo.Metadata[Constants.BinaryType].AsBsonValue.ToInt32();
    public RtEntityId? RtEntityId
    {
        get
        {
            if (!fsFileInfo.Metadata.Contains(Constants.RtEntityId))
            {
                return null;
            }

            var rtEntityId = fsFileInfo.Metadata[Constants.RtEntityId].AsBsonValue;
            return new RtEntityId(rtEntityId.AsString);
        }
    }
    public long Size => fsFileInfo.Length;
}
