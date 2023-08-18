using Meshmakers.Octo.Common.Shared;
using Meshmakers.Octo.SystematizedData.CkModel.Contracts;

// ReSharper disable MemberCanBePrivate.Global
// ReSharper disable ClassNeverInstantiated.Global
// ReSharper disable UnusedAutoPropertyAccessor.Global

namespace Meshmakers.Octo.Communication.Plugs.Contracts.DataTransferObjects;

/// <summary>
/// Represents a plug configuration for data transfer.
/// </summary>
public record PlugConfigurationDto
{
    /// <summary>
    /// Gets or sets the id of the plug.
    /// </summary>
    public OctoObjectId PlugRtId { get; init; }
    
    /// <summary>
    /// Gets or sets the server configurations of the plug.
    /// </summary>
    public IReadOnlyCollection<ServerConfigurationDto> ServerConfigurations { get; init; } = null!;


    /// <inheritdoc />
    public virtual bool Equals(PlugConfigurationDto? other)
    {
        if (other == null)
            return false;
        var b = ServerConfigurations.All(x => other.ServerConfigurations.Any(y=> y.Equals(x)));
        return PlugRtId.Equals(other.PlugRtId) && b;
    }

    /// <inheritdoc />
    public override int GetHashCode()
    {
        return HashCode.Combine(PlugRtId, ServerConfigurations);
    }
}