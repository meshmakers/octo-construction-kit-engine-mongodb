using Meshmakers.Octo.Runtime.Contracts.RepositoryEntities;
using MongoDB.Bson.Serialization;

namespace Meshmakers.Octo.Runtime.Engine.MongoDb.Serialization;

internal class RtRecordSerializer : BsonClassMapSerializer<RtRecord>
{
    public RtRecordSerializer()
        : base(BsonClassMap.LookupClassMap(typeof(RtRecord)))
    {
    }
    
    public override void Serialize(BsonSerializationContext context, BsonSerializationArgs args, RtRecord value)
    {
        var tmpEntity = new RtRecord(value.CkRecordId, value.Attributes);
        base.Serialize(context, args, tmpEntity);
    }
}