using System.Reflection;
using Meshmakers.Octo.Runtime.Contracts.RepositoryEntities;
using MongoDB.Bson;
using MongoDB.Bson.IO;
using MongoDB.Bson.Serialization;
using MongoDB.Bson.Serialization.Conventions;
using MongoDB.Bson.Serialization.Serializers;

namespace Meshmakers.Octo.Runtime.Engine.MongoDb.Serialization;

internal class RtEntityDiscriminatorConvention : IDiscriminatorConvention
{
    public RtEntityDiscriminatorConvention(string elementName)
    {
        if (elementName == null)
        {
            throw new ArgumentNullException(nameof(elementName));
        }

        if (elementName.IndexOf('\0') != -1)
        {
            throw new ArgumentException("Element names cannot contain nulls.", nameof(elementName));
        }

        ElementName = elementName;
    }

    public static RtEntityDiscriminatorConvention Instance { get; } = new("_t");

    public Type GetActualType(IBsonReader bsonReader, Type nominalType)
    {
        // the BsonReader is sitting at the value whose actual type needs to be found
        var bsonType = bsonReader.GetCurrentBsonType();
        if (bsonReader.State == BsonReaderState.Value)
        {
            Type? primitiveType = null;
            switch (bsonType)
            {
                case BsonType.Boolean:
                    primitiveType = typeof(bool);
                    break;
                case BsonType.Binary:
                    var bookmark = bsonReader.GetBookmark();
                    var binaryData = bsonReader.ReadBinaryData();
                    var subType = binaryData.SubType;
                    if (subType == BsonBinarySubType.UuidStandard || subType == BsonBinarySubType.UuidLegacy)
                    {
                        primitiveType = typeof(Guid);
                    }

                    bsonReader.ReturnToBookmark(bookmark);
                    break;
                case BsonType.DateTime:
                    primitiveType = typeof(DateTime);
                    break;
                case BsonType.Decimal128:
                    primitiveType = typeof(Decimal128);
                    break;
                case BsonType.Double:
                    primitiveType = typeof(double);
                    break;
                case BsonType.Int32:
                    primitiveType = typeof(int);
                    break;
                case BsonType.Int64:
                    primitiveType = typeof(long);
                    break;
                case BsonType.ObjectId:
                    primitiveType = typeof(ObjectId);
                    break;
                case BsonType.String:
                    primitiveType = typeof(string);
                    break;
            }

            // Type.IsAssignableFrom is extremely expensive, always perform a direct type check before calling Type.IsAssignableFrom
            if (primitiveType != null && (primitiveType == nominalType || nominalType.GetTypeInfo().IsAssignableFrom(primitiveType)))
            {
                return primitiveType;
            }
        }

        if (bsonType == BsonType.Document)
        {
            var bookmark = bsonReader.GetBookmark();
            bsonReader.ReadStartDocument();
            var actualType = nominalType;
            if (bsonReader.FindElement(ElementName))
            {
                var context = BsonDeserializationContext.CreateRoot(bsonReader);
                var discriminator = BsonValueSerializer.Instance.Deserialize(context);
                if (discriminator.IsBsonArray)
                {
                    discriminator = discriminator.AsBsonArray.Last(); // last item is leaf class discriminator
                }
                
                // We ignore the discriminator for List`1 - we handle that on our side.
                if (discriminator.AsString == "List`1")
                {
                    actualType =  typeof(List<object>);
                }
                // We ignore the discriminator "RtEntity" - we handle that on our side.
                else if (discriminator.AsString == "RtEntity" && nominalType.IsAssignableTo(typeof(RtEntity)))
                {
                    actualType = nominalType;
                }
                else
                {
                    actualType = BsonSerializer.LookupActualType(nominalType, discriminator);
                }
            }

            bsonReader.ReturnToBookmark(bookmark);
            return actualType;
        }

        return nominalType;
    }

    public BsonValue GetDiscriminator(Type nominalType, Type actualType)
    {
        return TypeNameDiscriminator.GetDiscriminator(actualType);
    }

    public string ElementName { get; }
}