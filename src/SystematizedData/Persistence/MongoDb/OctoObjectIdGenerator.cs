using Meshmakers.Octo.Common.Shared;
using Meshmakers.Octo.SystematizedData.CkModel.Contracts;
using MongoDB.Bson.Serialization;

namespace Meshmakers.Octo.SystematizedData.Persistence.MongoDb;

public class OctoObjectIdGenerator : IIdGenerator
{
    
    public object GenerateId(object container, object document)
    {
        return OctoObjectId.GenerateNewId();
    }

    public bool IsEmpty(object? id)
    {
        return id == null || (OctoObjectId)id == OctoObjectId.Empty;
    }
}