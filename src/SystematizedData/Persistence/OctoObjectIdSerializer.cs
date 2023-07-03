using System;
using Meshmakers.Octo.Common.Shared;
using MongoDB.Bson;
using MongoDB.Bson.Serialization;
using MongoDB.Bson.Serialization.Serializers;

namespace Meshmakers.Octo.SystematizedData.Persistence;

public class OctoObjectIdSerializer : StructSerializerBase<OctoObjectId>, IRepresentationConfigurable<OctoObjectIdSerializer>
{
    private readonly BsonType _representation;
    public OctoObjectIdSerializer()
        : this(BsonType.ObjectId)
    {
    }
    
    public OctoObjectIdSerializer(BsonType representation)
    {
        switch (representation)
        {
            case BsonType.ObjectId:
            case BsonType.String:
                break;

            default:
                var message = $"{representation} is not a valid representation for an ObjectIdSerializer.";
                throw new ArgumentException(message);
        }

        _representation = representation;
    }

    public override OctoObjectId Deserialize(BsonDeserializationContext context, BsonDeserializationArgs args)
    {
        var bsonReader = context.Reader;

        BsonType bsonType = bsonReader.GetCurrentBsonType();
        switch (bsonType)
        {
            case BsonType.ObjectId:
                return new OctoObjectId(bsonReader.ReadObjectId().ToByteArray());

            case BsonType.String:
                return OctoObjectId.Parse(bsonReader.ReadString());

            default:
                throw CreateCannotDeserializeFromBsonTypeException(bsonType);
        }
    }

    public override void Serialize(BsonSerializationContext context, BsonSerializationArgs args, OctoObjectId value)
    {
        var bsonWriter = context.Writer;

        switch (_representation)
        {
            case BsonType.ObjectId:
                bsonWriter.WriteObjectId(value.ToObjectId());
                break;

            case BsonType.String:
                bsonWriter.WriteString(value.ToString());
                break;

            default:
                var message = $"'{_representation}' is not a valid ObjectId representation.";
                throw new BsonSerializationException(message);
        }
    }

    public OctoObjectIdSerializer WithRepresentation(BsonType representation)
    {
        if (representation == _representation)
        {
            return this;
        }

        return new OctoObjectIdSerializer(representation);
    }

    public BsonType Representation => _representation;

    IBsonSerializer IRepresentationConfigurable.WithRepresentation(BsonType representation)
    {
        return WithRepresentation(representation);
    }
}