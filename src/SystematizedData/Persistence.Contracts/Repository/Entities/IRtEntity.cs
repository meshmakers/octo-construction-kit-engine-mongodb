using Meshmakers.Octo.Common.Shared;
using Persistence.Contracts;

namespace Meshmakers.Octo.SystematizedData.Persistence.DatabaseEntities;

public interface IRtEntity2
{
    /// <summary>
    ///     Gets or sets the runtime id
    /// </summary>
    OctoObjectId RtId { get; set; }

    /// <summary>
    ///     Returns the creation date time
    /// </summary>
    DateTime? RtCreationDateTime { get; set; }

    /// <summary>
    ///     Returns the last change date time
    /// </summary>
    DateTime? RtChangedDateTime { get; set; }

    /// <summary>
    ///     Gets or sets the construction kit id
    /// </summary>
    CkId<CkTypeId> CkId { get; set; }

    /// <summary>
    ///     Returns the well known name to access well known entities in a faster way
    /// </summary>
    string? RtWellKnownName { get; set; }

    /// <summary>
    ///     Returns an dictionary of attributes.
    /// </summary>
    /// <remarks>
    ///     Vor getting/setting values use the GetAttribute/SetAttribute-Methods
    /// </remarks>
    IReadOnlyDictionary<string, object?> Attributes { get; }

    TValue? GetAttributeValueOrDefault<TValue>(string attributeName, TValue? defaultValue = default)
        where TValue : struct;

    object? GetAttributeValueOrDefault(string attributeName, object? defaultValue = default);
    string? GetAttributeStringValueOrDefault(string attributeName, string? defaultValue = default);

    void SetAttributeValue(string attributeName, AttributeValueTypes attributeValueTypes,
        object? attributeValue);
}