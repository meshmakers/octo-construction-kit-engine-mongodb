// ReSharper disable MemberCanBePrivate.Global
// ReSharper disable ClassNeverInstantiated.Global
// ReSharper disable UnusedAutoPropertyAccessor.Global

using System.Text.Json.Serialization;
using Meshmakers.Octo.Common.Shared;

namespace Meshmakers.Octo.Communication.Sockets.Contracts.DataTransferObjects;

/// <summary>
/// Represents a socket configuration for data transfer.
/// </summary>
public record SocketConfigurationDto
{
    /// <summary>
    /// Constructor
    /// </summary>
    /// <param name="socketRtId">Id of the socket</param>
    [JsonConstructor]
    public SocketConfigurationDto(OctoObjectId socketRtId)
    {
        SocketRtId = socketRtId;
    }
    
    /// <summary>
    /// Gets or sets the id of the socket.
    /// </summary>
    public OctoObjectId SocketRtId { get; }
    
    /// <inheritdoc />
    public virtual bool Equals(SocketConfigurationDto? other)
    {
        if (other == null)
            return false;
        return SocketRtId.Equals(other.SocketRtId);
    }

    /// <inheritdoc />
    public override int GetHashCode()
    {
        return SocketRtId.GetHashCode();
    }
}