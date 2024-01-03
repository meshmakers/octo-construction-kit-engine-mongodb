using Meshmakers.Common.Shared;
using Meshmakers.Octo.Runtime.Contracts.RepositoryEntities;
using MongoDB.Bson.Serialization;
using MongoDB.Bson.Serialization.Options;
using MongoDB.Bson.Serialization.Serializers;

namespace Meshmakers.Octo.Runtime.Engine.MongoDb.Serialization;

internal class RtAttributeDictionarySerializer : DictionaryInterfaceImplementerSerializer<Dictionary<string, object?>>
{
    public RtAttributeDictionarySerializer() :
        base(DictionaryRepresentation.Document)
    {
    }


    public override void Serialize(BsonSerializationContext context, BsonSerializationArgs args,
        Dictionary<string, object?>? value)
    {
        if (value != null)
        {
            var dic = value.ToDictionary(d => d.Key.ToCamelCase(), d => d.Value);

            var bsonWriter = context.Writer;
            bsonWriter.WriteStartDocument();

            foreach (var keyValuePair in dic)
            {
                bsonWriter.WriteName(keyValuePair.Key);
                if (keyValuePair.Value == null)
                {
                    bsonWriter.WriteNull();
                    continue;
                }

                switch (keyValuePair.Value)
                {
                    case IEnumerable<string> enumerable:
                        bsonWriter.WriteStartArray();
                        foreach (var item in enumerable)
                        {
                            bsonWriter.WriteString(item);
                        }

                        bsonWriter.WriteEndArray();
                        break;
                    case IEnumerable<RtRecord> enumerable:
                        bsonWriter.WriteStartArray();
                        var recordSerializer = BsonSerializer.LookupSerializer(typeof(RtRecord));
                        foreach (var item in enumerable)
                        {
                            recordSerializer.Serialize(context, args, item);
                        }

                        bsonWriter.WriteEndArray();
                        break;
                    default:
                        var actualType = keyValuePair.Value.GetType();
                        var serializer = BsonSerializer.LookupSerializer(actualType);
                        serializer.Serialize(context, args, keyValuePair.Value);
                        break;
                }
            }

            bsonWriter.WriteEndDocument();
        }
        else
        {
            var bsonWriter = context.Writer;
            bsonWriter.WriteNull();
        }
    }

    public override Dictionary<string, object?> Deserialize(BsonDeserializationContext context,
        BsonDeserializationArgs args)
    {
        var dic = base.Deserialize(context, args);
        if (dic == null)
        {
            return null!;
        }

        var ret = new Dictionary<string, object?>();
        foreach (var pair in dic)
        {
            ret[pair.Key.ToPascalCase()] = pair.Value;
        }

        return ret;
    }
}