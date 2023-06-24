using Meshmakers.Octo.Communication.Sockets.Contracts.DataTransferObjects;
// ReSharper disable UnusedType.Global
// ReSharper disable UnusedMember.Global

namespace Meshmakers.Octo.Communication.Sockets.Contracts.Hubs;

/// <summary>
/// Interfaces of callbacks that can be called by the socket hub.
/// </summary>
public interface ISocketHubCallbacks
{
    /// <summary>
    /// Informs a socket that its configuration has been updated.
    /// </summary>
    /// <param name="tenantId">Tenant identifier</param>
    /// <param name="socketConfiguration">The socket configuration data transfer object</param>
    /// <returns></returns>
    Task SocketConfigurationUpdatedAsync(string tenantId, SocketConfigurationDto socketConfiguration);
}