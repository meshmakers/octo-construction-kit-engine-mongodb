using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;

namespace Meshmakers.Octo.Common.Shared.Exchange;

public static class RtSerializer
{
    public static async Task SerializeAsync(StreamWriter streamWriter, RtModelRoot model)
    {
        var options = new JsonSerializerOptions {  DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingDefault };

        await JsonSerializer.SerializeAsync(streamWriter.BaseStream, model, options);
    }

    public static async Task DeserializeAsync(StreamReader textReader, Func<RtEntity, Task> entityDeserializedAction,
        CancellationToken? cancellationToken = null)
    {
        var options = new JsonDocumentOptions {  };
        //
        // var startQueue = new Queue<Tuple<JsonToken, object?>>();
        // startQueue.Enqueue(new Tuple<JsonToken, object?>(JsonToken.StartObject, null));
        // startQueue.Enqueue(new Tuple<JsonToken, object?>(JsonToken.PropertyName, "entities"));
        // startQueue.Enqueue(new Tuple<JsonToken, object?>(JsonToken.StartArray, null));

        using var jsonDocument =await JsonDocument.ParseAsync(textReader.BaseStream, options);
        // while (await jsonDocument.RootElement.)
        {
            // if (startQueue.Count > 0)
            // {
            //     var data = startQueue.Dequeue();
            //     if (reader.TokenType == data.Item1 && Equals(reader.Value, data.Item2))
            //     {
            //         continue;
            //     }
            //
            //     throw new ModelParseException(
            //         "Missing structure of JSON file format. Ensure that file begins with { \"entities\" : [ {");
            // }
            //
            // if (cancellationToken != null && cancellationToken.Value.IsCancellationRequested)
            // {
            //     return;
            // }
            //
            // if (reader.TokenType == JsonToken.StartObject)
            // {
            //     var c = serializer.Deserialize<RtEntity>(reader);
            //     await entityDeserializedAction(c);
            // }
        }
    }
}
