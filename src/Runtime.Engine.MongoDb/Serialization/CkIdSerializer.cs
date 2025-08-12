using Meshmakers.Octo.ConstructionKit.Contracts;
using MongoDB.Bson;
using MongoDB.Bson.Serialization;
using MongoDB.Bson.Serialization.Serializers;

namespace Meshmakers.Octo.Runtime.Engine.MongoDb.Serialization;

public class CkIdSerializer<TKey, TKeySerializer> : SerializerBase<CkId<TKey>>,
    IRepresentationConfigurable<CkIdSerializer<TKey, TKeySerializer>> where TKey : IComparable<TKey>, ICkKey
    where TKeySerializer : SerializerBase<TKey>
{
    public CkIdSerializer()
        : this(BsonType.String)
    {
    }

    public CkIdSerializer(BsonType representation)
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

    public CkIdSerializer<TKey, TKeySerializer> WithRepresentation(BsonType representation)
    {
        if (representation == Representation)
        {
            return this;
        }

        return new CkIdSerializer<TKey, TKeySerializer>(representation);
    }

    public BsonType Representation { get; }

    IBsonSerializer IRepresentationConfigurable.WithRepresentation(BsonType representation)
    {
        return WithRepresentation(representation);
    }

    public override CkId<TKey> Deserialize(BsonDeserializationContext context, BsonDeserializationArgs args)
    {
        var bsonReader = context.Reader;

        var bsonType = bsonReader.GetCurrentBsonType();
        switch (bsonType)
        {
            case BsonType.String:
                return new CkId<TKey>(bsonReader.ReadString());

            default:
                throw CreateCannotDeserializeFromBsonTypeException(bsonType);
        }
    }

    public override void Serialize(BsonSerializationContext context, BsonSerializationArgs args, CkId<TKey>? value)
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
                var message = $"'{Representation}' is not a valid ObjectId representation.";
                throw new BsonSerializationException(message);
        }
    }
}
