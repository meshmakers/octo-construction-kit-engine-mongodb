using System.Diagnostics.CodeAnalysis;
using Meshmakers.Octo.ConstructionKit.Contracts;
using MongoDB.Bson;
using MongoDB.Bson.Serialization;
using MongoDB.Bson.Serialization.Serializers;

namespace Meshmakers.Octo.SystematizedData.Persistence;

public class CkIdSerializer<TKey, TKeySerializer> : StructSerializerBase<CkId<TKey>>, IRepresentationConfigurable<CkIdSerializer<TKey, TKeySerializer>> where TKey : struct, IComparable<TKey>, ICkKey where TKeySerializer : StructSerializerBase<TKey>
{
    private readonly BsonType _representation;
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

        _representation = representation;
    }

    public override CkId<TKey> Deserialize(BsonDeserializationContext context, BsonDeserializationArgs args)
    {
        var bsonReader = context.Reader;

        BsonType bsonType = bsonReader.GetCurrentBsonType();
        switch (bsonType)
        {
            case BsonType.String:
                return new CkId<TKey>(bsonReader.ReadString());

            default:
                throw CreateCannotDeserializeFromBsonTypeException(bsonType);
        }
    }

    public override void Serialize(BsonSerializationContext context, BsonSerializationArgs args, CkId<TKey> value)
    {
        var bsonWriter = context.Writer;

        switch (_representation)
        {
            case BsonType.String:
                bsonWriter.WriteString(value.ToString());
                break;

            default:
                var message = $"'{_representation}' is not a valid ObjectId representation.";
                throw new BsonSerializationException(message);
        }
    }

    public CkIdSerializer<TKey, TKeySerializer> WithRepresentation(BsonType representation)
    {
        if (representation == _representation)
        {
            return this;
        }

        return new CkIdSerializer<TKey, TKeySerializer>(representation);
    }

    public BsonType Representation => _representation;

    IBsonSerializer IRepresentationConfigurable.WithRepresentation(BsonType representation)
    {
        return WithRepresentation(representation);
    }
}