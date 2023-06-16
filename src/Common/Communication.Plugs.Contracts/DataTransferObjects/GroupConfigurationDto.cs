using Meshmakers.Octo.Common.Shared;
// ReSharper disable MemberCanBePrivate.Global
// ReSharper disable ClassNeverInstantiated.Global
// ReSharper disable UnusedAutoPropertyAccessor.Global

namespace Meshmakers.Octo.Communication.Plugs.Contracts.DataTransferObjects;

/// <summary>
/// Represents a group configuration for a plug for data transfer.
/// </summary>
public record GroupConfigurationDto
{
    /// <summary>
    /// Gets or sets the mappings of the group.
    /// </summary>
    public IReadOnlyCollection<MappingConfigurationDto> Mappings { get; init; } = null!;
    
    /// <summary>
    /// Gets or sets name of the group.
    /// </summary>
    public string Name { get; init; } = null!;
    
    /// <summary>
    /// Gets or sets the id of the group.
    /// </summary>
    public OctoObjectId Id { get; init; }

    /// <inheritdoc />
    public virtual bool Equals(GroupConfigurationDto? other)
    {
        if (other == null)
            return false;
        var b = Mappings.All(x => other.Mappings.Any(y => y.Equals(x)));
        return Name.Equals(other.Name) && Id.Equals(other.Id) && b;
    }

    /// <inheritdoc />
    public override int GetHashCode()
    {
        return HashCode.Combine(Mappings, Name, Id);
    }
}