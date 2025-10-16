using Meshmakers.Octo.ConstructionKit.Contracts;
using MongoDB.Bson;
using MongoDB.Bson.Serialization;
using MongoDB.Bson.Serialization.Serializers;

namespace Meshmakers.Octo.Runtime.Engine.MongoDb.Serialization;

public class RtCkIdSerializer<TKey, TKeySerializer> : SerializerBase<RtCkId<TKey>>,
    IRepresentationConfigurable<RtCkIdSerializer<TKey, TKeySerializer>> where TKey : IComparable<TKey>, ICkElementId
    where TKeySerializer : SerializerBase<TKey>
{
    public RtCkIdSerializer()
        : this(BsonType.String)
    {
    }

    public RtCkIdSerializer(BsonType representation)
    {
        switch (representation)
        {
            case BsonType.ObjectId:
            case BsonType.String:
                break;

            default:
                var message = $"{representation} is not a valid representation for an RtCkIdSerializer.";
                throw new ArgumentException(message);
        }

        Representation = representation;
    }

    public RtCkIdSerializer<TKey, TKeySerializer> WithRepresentation(BsonType representation)
    {
        if (representation == Representation)
        {
            return this;
        }

        return new RtCkIdSerializer<TKey, TKeySerializer>(representation);
    }

    public BsonType Representation { get; }

    IBsonSerializer IRepresentationConfigurable.WithRepresentation(BsonType representation)
    {
        return WithRepresentation(representation);
    }

    public override RtCkId<TKey> Deserialize(BsonDeserializationContext context, BsonDeserializationArgs args)
    {
        var bsonReader = context.Reader;

        var bsonType = bsonReader.GetCurrentBsonType();
        switch (bsonType)
        {
            case BsonType.String:
                return new RtCkId<TKey>(bsonReader.ReadString());

            default:
                throw CreateCannotDeserializeFromBsonTypeException(bsonType);
        }
    }

    public override void Serialize(BsonSerializationContext context, BsonSerializationArgs args, RtCkId<TKey>? value)
    {
        var bsonWriter = context.Writer;

        switch (Representation)
        {
            case BsonType.String:
                if (value == null)
                {
                    bsonWriter.WriteNull();
                    break;
                }
                bsonWriter.WriteString(value.ToString());
                break;

            default:
                var message = $"'{Representation}' is not a valid RtCkId representation.";
                throw new BsonSerializationException(message);
        }
    }
}
