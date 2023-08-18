using System.Text.Json;
using System.Text.Json.Serialization;

namespace Meshmakers.Octo.SystematizedData.CkModel.Contracts;

public class CkIdAttributeIdConverter : CkIdConverter<CkAttributeId>
{
    
}

public class CkIdTypeIdConverter : CkIdConverter<CkTypeId>
{
    
}

public class CkIdAssociationIdConverter : CkIdConverter<CkAssociationRoleId>
{
    
}

public class CkIdConverter<TKey> : JsonConverter<CkId<TKey>>where TKey : struct, IComparable<TKey>, ICkKey
{
    public override CkId<TKey> Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        var str = reader.TokenType == JsonTokenType.String
            ? reader.GetString()
            : throw ModelParseException.UnexpectedToken(nameof(CkModelId), reader.TokenType);
        return !string.IsNullOrEmpty(str) ? new CkId<TKey>(str) : throw ModelParseException.ValueCannotBeEmpty(nameof(CkModelId));
    }

    public override void Write(Utf8JsonWriter writer, CkId<TKey> value, JsonSerializerOptions options)
    {
        writer.WriteStringValue(value.ToString());
    }
}
