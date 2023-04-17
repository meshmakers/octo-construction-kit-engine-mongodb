using Meshmakers.Octo.Common.Shared;
using MongoDB.Bson;

namespace Meshmakers.Octo.Backend.Persistence;

public static class ObjectIdExtensions
{
    public static ObjectId ToObjectId(this OctoObjectId objectId)
    {
        return new ObjectId(objectId.ToString());
    }

    public static OctoObjectId ToOctoObjectId(this ObjectId objectId)
    {
        return new OctoObjectId(objectId.ToByteArray());
    }
}
