using System;
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Meshmakers.Octo.Common.Shared;

public class CkModelIdConverter : JsonConverter<CkModelId>
{
    public override CkModelId Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        var str = reader.TokenType == JsonTokenType.String
            ? reader.GetString()
            : throw ModelParseException.UnexpectedToken(nameof(CkModelId), reader.TokenType);
        return !string.IsNullOrEmpty(str) ? new CkModelId(str) : throw ModelParseException.ValueCannotBeEmpty(nameof(CkModelId));
    }

    public override void Write(Utf8JsonWriter writer, CkModelId value, JsonSerializerOptions options)
    {
        writer.WriteStringValue(value.ToString(CultureInfo.InvariantCulture));
    }
}
