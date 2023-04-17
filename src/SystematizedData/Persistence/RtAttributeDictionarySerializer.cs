using System.Collections.Generic;
using System.Linq;
using Meshmakers.Common.Shared;
using MongoDB.Bson.Serialization;
using MongoDB.Bson.Serialization.Options;
using MongoDB.Bson.Serialization.Serializers;

namespace Meshmakers.Octo.Backend.Persistence;

internal class RtAttributeDictionarySerializer : DictionarySerializerBase<Dictionary<string, object>>
{
    public RtAttributeDictionarySerializer() :
        base(DictionaryRepresentation.Document)
    {
    }

    protected override Dictionary<string, object> CreateInstance()
    {
        return new Dictionary<string, object>();
    }

    public override void Serialize(BsonSerializationContext context, BsonSerializationArgs args,
        Dictionary<string, object> value)
    {
        if (value != null)
        {
            var dic = value.ToDictionary(d => d.Key.ToCamelCase(), d => d.Value);
            BsonSerializer.Serialize(context.Writer, dic);
        }
        else
        {
            BsonSerializer.Serialize<object>(context.Writer, null);
        }
    }

    public override Dictionary<string, object> Deserialize(BsonDeserializationContext context,
        BsonDeserializationArgs args)
    {
        var dic = BsonSerializer.Deserialize<Dictionary<string, object>>(context.Reader);
        if (dic == null)
        {
            return null;
        }

        var ret = new Dictionary<string, object>();
        foreach (var pair in dic)
        {
            ret[pair.Key.ToPascalCase()] = pair.Value;
        }

        return ret;
    }
}
