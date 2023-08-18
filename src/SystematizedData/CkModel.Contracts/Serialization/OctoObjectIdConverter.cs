using System.Text.Json;
using System.Text.Json.Serialization;

namespace Meshmakers.Octo.SystematizedData.CkModel.Contracts.Serialization;

public class OctoObjectIdConverter : JsonConverter<OctoObjectId>
{
    public override OctoObjectId Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        var str = reader.TokenType == JsonTokenType.String
            ? reader.GetString()
            : throw new Exception(
                $"Unexpected token parsing ObjectId. Expected String, got {(object)reader.TokenType}.");
        return string.IsNullOrEmpty(str) ? OctoObjectId.Empty : new OctoObjectId(str);
    }

    public override void Write(Utf8JsonWriter writer, OctoObjectId value, JsonSerializerOptions options)
    {
        writer.WriteStringValue(value != OctoObjectId.Empty ? value.ToString() : string.Empty);
    }
}
