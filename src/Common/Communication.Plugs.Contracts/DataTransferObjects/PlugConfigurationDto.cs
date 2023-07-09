using System.Text.Json.Serialization;
using Meshmakers.Octo.Common.Shared;
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
    /// Constructor
    /// </summary>
    /// <param name="plugRtId">Id of the plug</param>
    /// <param name="serverConfigurations">Server configurations of the plug</param>
    [JsonConstructor]
    public PlugConfigurationDto(OctoObjectId plugRtId, IReadOnlyCollection<ServerConfigurationDto> serverConfigurations)
    {
        PlugRtId = plugRtId;
        ServerConfigurations = serverConfigurations;
    }
    
    /// <summary>
    /// Gets or sets the id of the plug.
    /// </summary>
    public OctoObjectId PlugRtId { get; }
    
    /// <summary>
    /// Gets or sets the server configurations of the plug.
    /// </summary>
    public IReadOnlyCollection<ServerConfigurationDto> ServerConfigurations { get; } = null!;


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
        return PlugRtId.GetHashCode() ^ ServerConfigurations.GetHashCode();
    }
}