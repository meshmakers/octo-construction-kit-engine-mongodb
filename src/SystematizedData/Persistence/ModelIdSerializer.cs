using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using Meshmakers.Octo.ConstructionKit.Contracts;
using MongoDB.Bson;
using MongoDB.Bson.Serialization;
using MongoDB.Bson.Serialization.Serializers;

namespace Meshmakers.Octo.SystematizedData.Persistence;

public class ModelIdSerializer : StructSerializerBase<CkModelId>, IBsonDocumentSerializer, IRepresentationConfigurable<ModelIdSerializer>
{
    private readonly BsonType _representation;
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

        _representation = representation;
    }
    
    public bool TryGetMemberSerializationInfo(string memberName, [UnscopedRef] out BsonSerializationInfo serializationInfo)
    {
        if (memberName == nameof(CkModelId.ModelId))
        {
            serializationInfo = new BsonSerializationInfo(memberName, new ModelIdSerializer(), typeof(string));
            return true;
        }


        serializationInfo = null!;
        return false;
    }

    public override CkModelId Deserialize(BsonDeserializationContext context, BsonDeserializationArgs args)
    {
        var bsonReader = context.Reader;

        BsonType bsonType = bsonReader.GetCurrentBsonType();
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

    public ModelIdSerializer WithRepresentation(BsonType representation)
    {
        if (representation == _representation)
        {
            return this;
        }

        return new ModelIdSerializer(representation);
    }

    public BsonType Representation => _representation;

    IBsonSerializer IRepresentationConfigurable.WithRepresentation(BsonType representation)
    {
        return WithRepresentation(representation);
    }
}