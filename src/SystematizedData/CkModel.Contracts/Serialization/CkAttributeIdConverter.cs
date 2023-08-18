using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Meshmakers.Octo.SystematizedData.CkModel.Contracts.Serialization;

public class CkAttributeIdConverter : JsonConverter<CkAttributeId>
{
    public override CkAttributeId Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        var str = reader.TokenType == JsonTokenType.String
            ? reader.GetString()
            : throw ModelParseException.UnexpectedToken(nameof(CkAttributeId), reader.TokenType);
        return !string.IsNullOrEmpty(str) ? new CkAttributeId(str) : throw ModelParseException.ValueCannotBeEmpty(nameof(CkAttributeId));
    }

    public override void Write(Utf8JsonWriter writer, CkAttributeId value, JsonSerializerOptions options)
    {
        writer.WriteStringValue(value.ToString(CultureInfo.InvariantCulture));
    }
}
