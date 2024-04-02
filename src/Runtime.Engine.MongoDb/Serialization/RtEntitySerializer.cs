using Meshmakers.Octo.Runtime.Contracts.MongoDb;
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
        var tmpEntity = new RtEntity(value.CkTypeId ?? throw OperationFailedException.CkTypeIdUndefined(), value.RtId, value.Attributes)
        {
            RtChangedDateTime = value.RtChangedDateTime,
            RtCreationDateTime = value.RtCreationDateTime,
            RtWellKnownName = value.RtWellKnownName
        };
        base.Serialize(context, args, tmpEntity);
    }

    public override RtEntity Deserialize(BsonDeserializationContext context, BsonDeserializationArgs args)
    {
        var o =  base.Deserialize(context, args);

        return o;
    }
}