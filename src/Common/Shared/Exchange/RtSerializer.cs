using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json;

namespace Meshmakers.Octo.Common.Shared.Exchange;

public static class RtSerializer
{
    public static void Serialize(StreamWriter streamWriter, RtModelRoot model)
    {
        var serializer = new JsonSerializer
        {
            DateTimeZoneHandling = DateTimeZoneHandling.Utc,
            NullValueHandling = NullValueHandling.Ignore
        };

        using (JsonWriter writer = new JsonTextWriter(streamWriter))
        {
            serializer.Serialize(writer, model);
        }
    }

    public static RtModelRoot? Deserialize(string s)
    {
        return Deserialize(new StringReader(s));
    }

    // ReSharper disable once MemberCanBePrivate.Global
    public static RtModelRoot? Deserialize(TextReader textReader)
    {
        var serializer = new JsonSerializer
        {
            DateTimeZoneHandling = DateTimeZoneHandling.Utc,
            NullValueHandling = NullValueHandling.Ignore
        };

        using (JsonReader reader = new JsonTextReader(textReader))
        {
            return serializer.Deserialize<RtModelRoot>(reader);
        }
    }

    public static async Task DeserializeAsync(TextReader textReader, Func<RtEntity?, Task> entityDeserializedAction,
        CancellationToken? cancellationToken = null)
    {
        var serializer = new JsonSerializer
        {
            DateTimeZoneHandling = DateTimeZoneHandling.Utc,
            NullValueHandling = NullValueHandling.Ignore
        };

        var startQueue = new Queue<Tuple<JsonToken, object?>>();
        startQueue.Enqueue(new Tuple<JsonToken, object?>(JsonToken.StartObject, null));
        startQueue.Enqueue(new Tuple<JsonToken, object?>(JsonToken.PropertyName, "entities"));
        startQueue.Enqueue(new Tuple<JsonToken, object?>(JsonToken.StartArray, null));

        var reader = new JsonTextReader(textReader);
        while (await reader.ReadAsync())
        {
            reader.SupportMultipleContent = true;

            if (startQueue.Count > 0)
            {
                var data = startQueue.Dequeue();
                if (reader.TokenType == data.Item1 && Equals(reader.Value, data.Item2))
                {
                    continue;
                }

                throw new ModelSerializerException(
                    "Missing structure of JSON file format. Ensure that file begins with { \"entities\" : [ {");
            }

            if (cancellationToken != null && cancellationToken.Value.IsCancellationRequested)
            {
                return;
            }

            if (reader.TokenType == JsonToken.StartObject)
            {
                var c = serializer.Deserialize<RtEntity>(reader);
                await entityDeserializedAction(c);
            }
        }
    }
}
