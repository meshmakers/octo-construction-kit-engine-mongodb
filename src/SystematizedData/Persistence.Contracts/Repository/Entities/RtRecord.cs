namespace Meshmakers.Octo.SystematizedData.Persistence.DatabaseEntities;

public class RtRecord
{
    public string? GetAttributeStringValueOrDefault(string attributeName, string? defaultValue = default)
    {
        return null;
    }

    public string GetAttributeStringValue(string attributeName)
    {
        return "";
    }

    public TValue? GetAttributeValueOrDefault<TValue>(string attributeName, TValue? defaultValue = default)
        where TValue : struct
    {
        return null;
    }

    public void SetAttributeValue(string attributeName, AttributeValueTypes attributeValueTypes,
        object? attributeValue)
    {
    }
    
    public void SetAttributeValueNonNullable(string attributeName, AttributeValueTypes attributeValueTypes,
        object attributeValue)
    {
    }
}