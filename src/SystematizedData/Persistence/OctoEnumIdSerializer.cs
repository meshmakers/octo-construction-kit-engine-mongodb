using System.Globalization;
using Meshmakers.Octo.ConstructionKit.Contracts;
using MongoDB.Bson;
using MongoDB.Bson.Serialization;
using MongoDB.Bson.Serialization.Serializers;

namespace Meshmakers.Octo.SystematizedData.Persistence;

public class OctoEnumIdSerializer : StructSerializerBase<CkEnumId>, IRepresentationConfigurable<OctoEnumIdSerializer>
{
    private readonly BsonType _representation;
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

        _representation = representation;
    }

    public override CkEnumId Deserialize(BsonDeserializationContext context, BsonDeserializationArgs args)
    {
        var bsonReader = context.Reader;

        BsonType bsonType = bsonReader.GetCurrentBsonType();
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

        switch (_representation)
        {
            case BsonType.String:
                bsonWriter.WriteString(value.ToString(CultureInfo.InvariantCulture));
                break;

            default:
                var message = $"'{_representation}' is not a valid ObjectId representation.";
                throw new BsonSerializationException(message);
        }
    }

    public OctoEnumIdSerializer WithRepresentation(BsonType representation)
    {
        if (representation == _representation)
        {
            return this;
        }

        return new OctoEnumIdSerializer(representation);
    }

    public BsonType Representation => _representation;

    IBsonSerializer IRepresentationConfigurable.WithRepresentation(BsonType representation)
    {
        return WithRepresentation(representation);
    }
}