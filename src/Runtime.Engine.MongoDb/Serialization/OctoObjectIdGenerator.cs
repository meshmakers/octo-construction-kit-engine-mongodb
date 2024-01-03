using Meshmakers.Octo.ConstructionKit.Contracts;
using MongoDB.Bson.Serialization;

namespace Meshmakers.Octo.Runtime.Engine.MongoDb.Serialization;

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