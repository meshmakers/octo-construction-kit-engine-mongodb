using Meshmakers.Octo.Runtime.Contracts.RepositoryEntities;
using MongoDB.Bson.Serialization;

namespace Meshmakers.Octo.Runtime.Engine.MongoDb.Serialization;

internal class RtEntitySerializer : BsonClassMapSerializer<RtEntity>
{
    public RtEntitySerializer()
        : base(BsonClassMap.LookupClassMap(typeof(RtEntity)))
    {
    }
    
    public override void Serialize(BsonSerializationContext context, BsonSerializationArgs args, RtEntity value)
    {
        var tmpEntity = new RtEntity(value.CkTypeId, value.RtId, value.Attributes);
        base.Serialize(context, args, tmpEntity);
    }
}