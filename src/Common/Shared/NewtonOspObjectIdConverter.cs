using System;
using Newtonsoft.Json;

namespace Meshmakers.Octo.Common.Shared;

public class NewtonOctoObjectIdConverter : JsonConverter
{
    public override void WriteJson(JsonWriter writer, object? value, JsonSerializer serializer)
    {
        if (value is OctoObjectId objectId)
        {
            writer.WriteValue(objectId != OctoObjectId.Empty ? objectId.ToString() : string.Empty);
        }
        else
        {
            throw new Exception("Expected ObjectId value.");
        }
    }

    public override object ReadJson(JsonReader reader, Type objectType, object? existingValue,
        JsonSerializer serializer)
    {
        if (reader.TokenType != JsonToken.String)
        {
            throw new Exception($"Unexpected token parsing ObjectId. Expected String, got {reader.TokenType}.");
        }

        if (reader.Value == null)
        {
            return OctoObjectId.Empty;
        }

        var value = (string)reader.Value;
        return string.IsNullOrEmpty(value) ? OctoObjectId.Empty : new OctoObjectId(value);
    }

    public override bool CanConvert(Type objectType)
    {
        return typeof(OctoObjectId).IsAssignableFrom(objectType);
    }
}
