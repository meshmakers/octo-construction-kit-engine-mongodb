using Meshmakers.Octo.SystematizedData.Persistence.DatabaseEntities;
using Persistence.SystemCkModel;

namespace Meshmakers.Octo.SystematizedData.Persistence.CkModelEntities;

/// <summary>
/// Represents a system configuration.
/// </summary>
[CkId(SystemCkModel.SystemCkModelId, SystemCkModel.SystemConfigurationTypeId)]
public class RtSystemConfiguration : RtEntity
{
    /// <summary>
    /// Returns the configuration value.
    /// </summary>
    public string? ConfigurationValue
    {
        get => GetAttributeStringValueOrDefault(nameof(ConfigurationValue));
        set => SetAttributeValue(nameof(ConfigurationValue), AttributeValueTypes.String, value);
    }
}