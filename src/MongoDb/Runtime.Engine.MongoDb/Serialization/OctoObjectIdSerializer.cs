using Meshmakers.Octo.ConstructionKit.Contracts;
using MongoDB.Bson;
using MongoDB.Bson.Serialization;
using MongoDB.Bson.Serialization.Serializers;

namespace Meshmakers.Octo.Runtime.Engine.MongoDb.Serialization;

public class OctoObjectIdSerializer : StructSerializerBase<OctoObjectId>, IRepresentationConfigurable<OctoObjectIdSerializer>
{
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

        Representation = representation;
    }

    public OctoObjectIdSerializer WithRepresentation(BsonType representation)
    {
        if (representation == Representation) return this;

        return new OctoObjectIdSerializer(representation);
    }

    public BsonType Representation { get; }

    IBsonSerializer IRepresentationConfigurable.WithRepresentation(BsonType representation)
    {
        return WithRepresentation(representation);
    }

    public override OctoObjectId Deserialize(BsonDeserializationContext context, BsonDeserializationArgs args)
    {
        var bsonReader = context.Reader;

        var bsonType = bsonReader.GetCurrentBsonType();
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

        switch (Representation)
        {
            case BsonType.ObjectId:
                bsonWriter.WriteObjectId(value.ToObjectId());
                break;

            case BsonType.String:
                bsonWriter.WriteString(value.ToString());
                break;

            default:
                var message = $"'{Representation}' is not a valid ObjectId representation.";
                throw new BsonSerializationException(message);
        }
    }
}