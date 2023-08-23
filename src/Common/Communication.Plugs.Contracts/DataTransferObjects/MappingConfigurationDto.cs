using Meshmakers.Octo.ConstructionKit.Contracts;


// ReSharper disable MemberCanBePrivate.Global
// ReSharper disable ClassNeverInstantiated.Global
// ReSharper disable UnusedAutoPropertyAccessor.Global

namespace Meshmakers.Octo.Communication.Plugs.Contracts.DataTransferObjects;

/// <summary>
/// Represents a mapping configuration for a plug for data transfer.
/// </summary>
public record MappingConfigurationDto
{
    /// <summary>
    /// Gets or sets name of the mapping.
    /// </summary>
    public string Name { get; init; } = null!;
    
    /// <summary>
    /// Gets or sets the id of the mapping.
    /// </summary>
    public OctoObjectId Id { get; init; }
    
    /// <summary>
    /// Gets or sets the configuration of the mapping. This is a JSON string.
    /// </summary>
    public string Configuration { get; init; } = null!;

    /// <inheritdoc />
    public virtual bool Equals(MappingConfigurationDto? other)
    {
        if (other == null)
            return false;
        return Name.Equals(other.Name) && Id.Equals(other.Id) && Configuration.Equals(other.Configuration);
    }

    /// <inheritdoc />
    public override int GetHashCode()
    {
        return HashCode.Combine(Name, Id, Configuration);
    }
}