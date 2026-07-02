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

            // A record is identified by its ckRecordId (the CK record type), NOT by the _t
            // discriminator. Detect it up-front so records always deserialize to RtRecord, whether
            // _t is absent (the normal case — GetDiscriminator returns null for RtRecord types) or
            // carries a legacy hierarchical value such as ["RtRecord","RtUserLoginRecord"] written by
            // an older build or a pod that lost the discriminator-convention registration race. This
            // mirrors OctoObjectListSerializer's ckRecordId-first handling and, crucially, avoids ever
            // calling LookupActualType on a *Record discriminator that is not registered in this
            // process — which throws "Unknown discriminator value" and broke tenant-wide external /
            // EntraID login (AB#4291 / AB#3321).
            if ((nominalType == typeof(object) || nominalType.IsAssignableTo(typeof(RtRecord)))
                && bsonReader.FindElement("ckRecordId"))
            {
                bsonReader.ReturnToBookmark(bookmark);
                return typeof(RtRecord);
            }

            bsonReader.ReturnToBookmark(bookmark);
            bsonReader.ReadStartDocument();

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
                    actualType = typeof(List<object>);
                }
                // We ignore the discriminator "RtEntity" - we handle that on our side.
                else if (discriminator.AsString == "RtEntity" && nominalType.IsAssignableTo(typeof(RtEntity)))
                {
                    actualType = nominalType;
                }
                // Handle RtRecord discriminators - the actual type is determined by CkRecordId, not by _t
                // This provides backward compatibility for old data with discriminators like "RtUserClaimRecord"
                else if (nominalType.IsAssignableTo(typeof(RtRecord)) || discriminator.AsString.EndsWith("Record"))
                {
                    actualType = typeof(RtRecord);
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
        if (actualType.IsAssignableTo(typeof(RtEntity)))
        {
            return "RtEntity";
        }

        // Don't write discriminator for RtRecord types - the type is determined by CkRecordId
        if (actualType.IsAssignableTo(typeof(RtRecord)))
        {
            return null!;
        }

        return TypeNameDiscriminator.GetDiscriminator(actualType);
    }

    public string ElementName { get; }
}