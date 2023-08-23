using Meshmakers.Octo.Communication.Sockets.Contracts.DataTransferObjects;
using Meshmakers.Octo.ConstructionKit.Contracts;


namespace Meshmakers.Octo.Communication.Sockets.Contracts.Hubs;

/// <summary>
/// Interface of the socket hub that is responsible for registering and unregistering sockets and managing their state.
/// </summary>
public interface ISocketHub
{
    /// <summary>
    /// Registers a socket at the communication controller
    /// </summary>
    /// <param name="socketRtId">Object identifier of the socket</param>
    /// <returns></returns>
    Task<SocketConfigurationDto> RegisterSocketAsync(OctoObjectId socketRtId);
    
    /// <summary>
    /// Unregisters a socket from the communication controller
    /// </summary>
    /// <param name="socketRtId">Object identifier of the socket</param>
    /// <returns></returns>
    Task UnRegisterSocketAsync(OctoObjectId socketRtId);
}