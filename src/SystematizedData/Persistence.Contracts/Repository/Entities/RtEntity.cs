using Meshmakers.Octo.Common.Shared;
using Persistence.Contracts;

namespace Meshmakers.Octo.SystematizedData.Persistence.DatabaseEntities;

/// <summary>
///     Represents an entity, based on information of the construction kit type
/// </summary>
public class RtEntity
{
    // ReSharper disable once FieldCanBeMadeReadOnly.Local
    private Dictionary<string, object?> _attributes;

    /// <summary>
    ///     Constructor
    /// </summary>
    public RtEntity()
    {
        _attributes = new Dictionary<string, object?>();
    }

    /// <summary>
    ///     Gets or sets the runtime id
    /// </summary>
    public OctoObjectId RtId { get; set; }

    /// <summary>
    ///     Returns the creation date time
    /// </summary>
    public DateTime? RtCreationDateTime { get; set; }

    /// <summary>
    ///     Returns the last change date time
    /// </summary>
    public DateTime? RtChangedDateTime { get; set; }

    /// <summary>
    ///     Gets or sets the construction kit id
    /// </summary>
    public CkId<CkTypeId> CkId { get; set; }

    /// <summary>
    ///     Returns the well known name to access well known entities in a faster way
    /// </summary>
    public string? RtWellKnownName { get; set; }

    /// <summary>
    ///     Returns an dictionary of attributes.
    /// </summary>
    /// <remarks>
    ///     Vor getting/setting values use the GetAttribute/SetAttribute-Methods
    /// </remarks>
    public IReadOnlyDictionary<string, object?> Attributes => _attributes;
    
    

    public TValue GetAttributeValueOrStandard<TValue>(string attributeName, TValue standardValue = default)
        where TValue : struct
    {
        if (!Attributes.TryGetValue(attributeName, out var value))
        {
            return standardValue;
        }

        if (value == null)
        {
            return standardValue;
        }

        // Because Convert.ChangeType cannot convert to enum types
        if (typeof(TValue).IsEnum)
        {
            return (TValue)Enum.ToObject(typeof(TValue), value);
        }

        return (TValue)Convert.ChangeType(value, typeof(TValue));
    }
    
    // ReSharper disable once MemberCanBeProtected.Global
    public TValue? GetAttributeValueOrDefault<TValue>(string attributeName, TValue? defaultValue = default)
        where TValue : struct
    {
        if (!Attributes.TryGetValue(attributeName, out var value))
        {
            return defaultValue;
        }

        if (value == null)
        {
            return defaultValue;
        }

        // Because Convert.ChangeType cannot convert to enum types
        if (typeof(TValue).IsEnum)
        {
            return (TValue)Enum.ToObject(typeof(TValue), value);
        }

        return (TValue)Convert.ChangeType(value, typeof(TValue));
    }

    public object? GetAttributeValueOrDefault(string attributeName, object? defaultValue = default)
    {
        if (!Attributes.TryGetValue(attributeName, out var value))
        {
            return defaultValue;
        }

        return value;
    }

    public string? GetAttributeStringValueOrDefault(string attributeName, string? defaultValue = default)
    {
        if (!Attributes.TryGetValue(attributeName, out var value))
        {
            return defaultValue;
        }

        return (string?)value;
    }
    
    public string GetAttributeStringValue(string attributeName)
    {
        if (!Attributes.TryGetValue(attributeName, out var value) || value == null)
        {
            throw InvalidAttributeValueException.CannotBeNull(RtId, CkId, attributeName);
        }

        return (string)value;
    }

    public void SetAttributeValue(string attributeName, AttributeValueTypes attributeValueTypes,
        object? attributeValue)
    {
        _attributes[attributeName] = ConvertAttributeValue(attributeValueTypes, attributeValue);
    }

    public static object? ConvertAttributeValue(AttributeValueTypes attributeValueTypes, object? value)
    {
        if (value == null)
        {
            return null;
        }

        switch (attributeValueTypes)
        {
            case AttributeValueTypes.String:
                if (value is string)
                {
                    return value;
                }

                return value.ToString();
            case AttributeValueTypes.Double:
                if (value is double)
                {
                    return value;
                }

                if (double.TryParse(value.ToString(), out var doubleResult))
                {
                    return doubleResult;
                }

                break;
            case AttributeValueTypes.Boolean:
                if (value is bool)
                {
                    return value;
                }

                if (bool.TryParse(value.ToString(), out var boolResult))
                {
                    return boolResult;
                }

                break;
            case AttributeValueTypes.Int:
                if (value is int)
                {
                    return value;
                }

                if (int.TryParse(value.ToString(), out var intResult))
                {
                    return intResult;
                }

                break;
            case AttributeValueTypes.DateTime:
                value = Convert.ToDateTime(value);
                break;
        }

        return value;
    }
}