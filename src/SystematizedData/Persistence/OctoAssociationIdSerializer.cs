using System;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using Meshmakers.Octo.Common.Shared;
using MongoDB.Bson;
using MongoDB.Bson.Serialization;
using MongoDB.Bson.Serialization.Serializers;

namespace Meshmakers.Octo.SystematizedData.Persistence;

public class OctoAssociationIdSerializer : StructSerializerBase<CkAssociationRoleId>, IRepresentationConfigurable<OctoAssociationIdSerializer>
{
    private readonly BsonType _representation;
    public OctoAssociationIdSerializer()
        : this(BsonType.String)
    {
    }
    
    public OctoAssociationIdSerializer(BsonType representation)
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

    public override CkAssociationRoleId Deserialize(BsonDeserializationContext context, BsonDeserializationArgs args)
    {
        var bsonReader = context.Reader;

        BsonType bsonType = bsonReader.GetCurrentBsonType();
        switch (bsonType)
        {
            case BsonType.String:
                return new CkAssociationRoleId(bsonReader.ReadString());

            default:
                throw CreateCannotDeserializeFromBsonTypeException(bsonType);
        }
    }

    public override void Serialize(BsonSerializationContext context, BsonSerializationArgs args, CkAssociationRoleId value)
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

    public OctoAssociationIdSerializer WithRepresentation(BsonType representation)
    {
        if (representation == _representation)
        {
            return this;
        }

        return new OctoAssociationIdSerializer(representation);
    }

    public BsonType Representation => _representation;

    IBsonSerializer IRepresentationConfigurable.WithRepresentation(BsonType representation)
    {
        return WithRepresentation(representation);
    }
}