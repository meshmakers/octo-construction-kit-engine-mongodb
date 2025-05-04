using Meshmakers.Octo.Runtime.Contracts.RepositoryEntities;

using MongoDB.Bson;
using MongoDB.Bson.IO;
using MongoDB.Bson.Serialization;
using MongoDB.Bson.Serialization.Serializers;

namespace Meshmakers.Octo.Runtime.Engine.MongoDb.Serialization;

public class OctoObjectListSerializer : SerializerBase<List<object>>
{
    public override List<object> Deserialize(BsonDeserializationContext context, BsonDeserializationArgs args)
    {
        var bsonReader = context.Reader;

        bsonReader.ReadStartArray();

        var list = new List<object>();
        
        while (bsonReader.ReadBsonType() != BsonType.EndOfDocument)
        {
            switch (bsonReader.GetCurrentBsonType())
            {
                case BsonType.String:
                    var str = bsonReader.ReadString();
                    list.Add(str);
                    break;
                case BsonType.Int32:
                    var int32 = bsonReader.ReadInt32();
                    list.Add(int32);
                    break;
                case BsonType.Int64:
                    var int64 = bsonReader.ReadInt64();
                    list.Add(int64);
                    break;
                case BsonType.Double:
                    var dbl = bsonReader.ReadDouble();
                    list.Add(dbl);
                    break;
                case BsonType.Boolean:
                    var bln = bsonReader.ReadBoolean();
                    list.Add(bln);
                    break;
                case BsonType.DateTime:
                    var dt = bsonReader.ReadDateTime();
                    list.Add(dt);
                    break;
                case BsonType.Document:
                    var bookmark = bsonReader.GetBookmark();
 
                    bsonReader.ReadStartDocument();
                    if (bsonReader.FindElement("_t"))
                    {
                        var discriminator = BsonValueSerializer.Instance.Deserialize(context);
                        if (discriminator.IsBsonArray)
                        {
                            discriminator = discriminator.AsBsonArray.Last(); // last item is leaf class discriminator
                        }
                        
                        var actualType = BsonSerializer.LookupActualType(typeof(object), discriminator);
                        
                        var serializer = BsonSerializer.LookupSerializer(actualType);
                        
                        bsonReader.ReturnToBookmark(bookmark);
                        var context2 = BsonDeserializationContext.CreateRoot(bsonReader);
                        var value = serializer.Deserialize(context2);
                        list.Add(value);
                    }

                    break;
                default:
                    throw OctoSerializationException.UnsupportedBsonType(bsonReader.GetCurrentBsonType());
            }
        }
        
        bsonReader.ReadEndArray();
        
        return list;
    }

    public override void Serialize(BsonSerializationContext context, BsonSerializationArgs args, List<object> value)
    {
        var bsonWriter = context.Writer;
        
        var firstItem = value.FirstOrDefault();
        if (firstItem == null)
        {
            bsonWriter.WriteNull();
            return;
        }
        
        bsonWriter.WriteStartArray();
        
        foreach (var item in value)
        {
            switch (Type.GetTypeCode(firstItem.GetType()))
            {
                case TypeCode.String:
                    bsonWriter.WriteString(item.ToString());
                    break;
                case TypeCode.Int32:
                    bsonWriter.WriteInt32((int)item);
                    break;
                case TypeCode.Int64:
                    bsonWriter.WriteInt64((long)item);
                    break;
                case TypeCode.Double:
                    bsonWriter.WriteDouble((double)item);
                    break;
                case TypeCode.Boolean:
                    bsonWriter.WriteBoolean((bool)item);
                    break;
                case TypeCode.DateTime:
                    bsonWriter.WriteDateTime(((DateTime)item).ToUniversalTime().Ticks);
                    break;
                case TypeCode.Object:
                    if (firstItem is RtRecord)
                    {
                        var rtRecord = (RtRecord)item;
                        var serializer = BsonSerializer.LookupSerializer(rtRecord.GetType());
                        serializer.Serialize(context, args, rtRecord);
                    }
                    else
                    {
                        throw OctoSerializationException.UnsupportedType(item.GetType());
                    }
                    break;
               default:
                   throw OctoSerializationException.UnsupportedType(item.GetType());
                    
            }
        }
        
        bsonWriter.WriteEndArray();
    }
}
