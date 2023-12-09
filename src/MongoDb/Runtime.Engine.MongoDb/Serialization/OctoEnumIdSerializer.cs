using System.Globalization;
using Meshmakers.Octo.ConstructionKit.Contracts;
using MongoDB.Bson;
using MongoDB.Bson.Serialization;
using MongoDB.Bson.Serialization.Serializers;

namespace Meshmakers.Octo.Runtime.Engine.MongoDb.Serialization;

public class OctoEnumIdSerializer : StructSerializerBase<CkEnumId>, IRepresentationConfigurable<OctoEnumIdSerializer>
{
    public OctoEnumIdSerializer()
        : this(BsonType.String)
    {
    }

    public OctoEnumIdSerializer(BsonType representation)
    {
        switch (representation)
        {
            case BsonType.String:
                break;

            default:
                var message = $"{representation} is not a valid representation for an ObjectIdSerializer.";
                throw new ArgumentException(message);
        }

        Representation = representation;
    }

    public OctoEnumIdSerializer WithRepresentation(BsonType representation)
    {
        if (representation == Representation) return this;

        return new OctoEnumIdSerializer(representation);
    }

    public BsonType Representation { get; }

    IBsonSerializer IRepresentationConfigurable.WithRepresentation(BsonType representation)
    {
        return WithRepresentation(representation);
    }

    public override CkEnumId Deserialize(BsonDeserializationContext context, BsonDeserializationArgs args)
    {
        var bsonReader = context.Reader;

        var bsonType = bsonReader.GetCurrentBsonType();
        switch (bsonType)
        {
            case BsonType.String:
                return new CkEnumId(bsonReader.ReadString());

            default:
                throw CreateCannotDeserializeFromBsonTypeException(bsonType);
        }
    }

    public override void Serialize(BsonSerializationContext context, BsonSerializationArgs args, CkEnumId value)
    {
        var bsonWriter = context.Writer;

        switch (Representation)
        {
            case BsonType.String:
                bsonWriter.WriteString(value.ToString(CultureInfo.InvariantCulture));
                break;

            default:
                var message = $"'{Representation}' is not a valid ObjectId representation.";
                throw new BsonSerializationException(message);
        }
    }
}