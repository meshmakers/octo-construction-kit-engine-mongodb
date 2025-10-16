using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using Meshmakers.Octo.ConstructionKit.Contracts;
using MongoDB.Bson;
using MongoDB.Bson.Serialization;
using MongoDB.Bson.Serialization.Serializers;

namespace Meshmakers.Octo.Runtime.Engine.MongoDb.Serialization;

public class ModelIdSerializer : SerializerBase<CkModelId>, IBsonDocumentSerializer, IRepresentationConfigurable<ModelIdSerializer>
{
    public ModelIdSerializer()
        : this(BsonType.String)
    {
    }

    public ModelIdSerializer(BsonType representation)
    {
        switch (representation)
        {
            case BsonType.String:
                break;

            default:
                var message = $"{representation} is not a valid representation for an OctoModelIdSerializer.";
                throw new ArgumentException(message);
        }

        Representation = representation;
    }

    public bool TryGetMemberSerializationInfo(string memberName, [UnscopedRef] out BsonSerializationInfo serializationInfo)
    {
        if (memberName == nameof(CkModelId.Name))
        {
            serializationInfo = new BsonSerializationInfo(memberName, new ModelIdSerializer(), typeof(string));
            return true;
        }


        serializationInfo = null!;
        return false;
    }

    public ModelIdSerializer WithRepresentation(BsonType representation)
    {
        if (representation == Representation)
        {
            return this;
        }

        return new ModelIdSerializer(representation);
    }

    public BsonType Representation { get; }

    IBsonSerializer IRepresentationConfigurable.WithRepresentation(BsonType representation)
    {
        return WithRepresentation(representation);
    }

    public override CkModelId Deserialize(BsonDeserializationContext context, BsonDeserializationArgs args)
    {
        var bsonReader = context.Reader;

        var bsonType = bsonReader.GetCurrentBsonType();
        switch (bsonType)
        {
            case BsonType.String:
                return new CkModelId(bsonReader.ReadString());

            default:
                throw CreateCannotDeserializeFromBsonTypeException(bsonType);
        }
    }

    public override void Serialize(BsonSerializationContext context, BsonSerializationArgs args, CkModelId value)
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
