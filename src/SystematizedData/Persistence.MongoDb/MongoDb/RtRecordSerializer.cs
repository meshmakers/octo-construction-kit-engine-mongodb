using System.Diagnostics.CodeAnalysis;
using Meshmakers.Octo.Runtime.Contracts.RepositoryEntities;
using MongoDB.Bson;
using MongoDB.Bson.Serialization;
using MongoDB.Bson.Serialization.Serializers;

namespace Meshmakers.Octo.SystematizedData.Persistence.MongoDb;

public class RtRecordSerializer: SerializerBase<RtRecord>, IBsonDocumentSerializer, IRepresentationConfigurable<RtRecordSerializer>
{
    private readonly BsonType _representation;
    public RtRecordSerializer()
        : this(BsonType.Document)
    {
    }

    public override void Serialize(BsonSerializationContext context, BsonSerializationArgs args, RtRecord value)
    {
        base.Serialize(context, args, value);
    }

    public RtRecordSerializer(BsonType representation)
    {
        switch (representation)
        {
            case BsonType.Document:
                break;

            default:
                var message = $"{representation} is not a valid representation for an RtRecordSerializer.";
                throw new ArgumentException(message);
        }

        _representation = representation;
    }
        
    public bool TryGetMemberSerializationInfo(string memberName, [UnscopedRef] out BsonSerializationInfo? serializationInfo)
    {
        serializationInfo = null;
        return false;
    }

    public RtRecordSerializer WithRepresentation(BsonType representation)
    {
        if (representation == _representation)
        {
            return this;
        }

        return new RtRecordSerializer(representation);
    }

    public BsonType Representation => _representation;
    
    IBsonSerializer IRepresentationConfigurable.WithRepresentation(BsonType representation)
    {
        return WithRepresentation(representation);
    }
    
    
}