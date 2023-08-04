using System;
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Meshmakers.Octo.Common.Shared;

public class CkAssociationIdConverter : JsonConverter<CkAssociationId>
{
    public override CkAssociationId Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        var str = reader.TokenType == JsonTokenType.String
            ? reader.GetString()
            : throw ModelParseException.UnexpectedToken(nameof(CkAssociationId), reader.TokenType);
        return !string.IsNullOrEmpty(str) ? new CkAssociationId(str) : throw ModelParseException.ValueCannotBeEmpty(nameof(CkAssociationId));
    }

    public override void Write(Utf8JsonWriter writer, CkAssociationId value, JsonSerializerOptions options)
    {
        writer.WriteStringValue(value.ToString(CultureInfo.InvariantCulture));
    }
}
