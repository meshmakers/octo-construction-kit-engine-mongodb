using System;
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Meshmakers.Octo.Common.Shared;

public class CkTypeIdConverter : JsonConverter<CkTypeId>
{
    public override CkTypeId Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        var str = reader.TokenType == JsonTokenType.String
            ? reader.GetString()
            : throw ModelParseException.UnexpectedToken(nameof(CkTypeId), reader.TokenType);
        return !string.IsNullOrEmpty(str) ? new CkTypeId(str) : throw ModelParseException.ValueCannotBeEmpty(nameof(CkTypeId));
    }

    public override void Write(Utf8JsonWriter writer, CkTypeId value, JsonSerializerOptions options)
    {
        writer.WriteStringValue(value.ToString(CultureInfo.InvariantCulture));
    }
}
