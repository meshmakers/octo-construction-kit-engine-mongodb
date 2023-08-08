using System;
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Meshmakers.Octo.Common.Shared;

public class CkAssociationIdConverter : JsonConverter<CkAssociationRoleId>
{
    public override CkAssociationRoleId Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        var str = reader.TokenType == JsonTokenType.String
            ? reader.GetString()
            : throw ModelParseException.UnexpectedToken(nameof(CkAssociationRoleId), reader.TokenType);
        return !string.IsNullOrEmpty(str) ? new CkAssociationRoleId(str) : throw ModelParseException.ValueCannotBeEmpty(nameof(CkAssociationRoleId));
    }

    public override void Write(Utf8JsonWriter writer, CkAssociationRoleId value, JsonSerializerOptions options)
    {
        writer.WriteStringValue(value.ToString(CultureInfo.InvariantCulture));
    }
}
