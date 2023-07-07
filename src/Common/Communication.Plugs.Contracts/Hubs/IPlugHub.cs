using Meshmakers.Octo.Common.Shared;
using Meshmakers.Octo.Communication.Plugs.Contracts.DataTransferObjects;
// ReSharper disable UnusedType.Global
// ReSharper disable UnusedMember.Global

namespace Meshmakers.Octo.Communication.Plugs.Contracts.Hubs;

/// <summary>
/// Interface of the plug hub that is responsible for registering and unregistering plugs and managing their state.
/// </summary>
public interface IPlugHub
{
    /// <summary>
    /// Registers a plug at the communication controller
    /// </summary>
    /// <param name="plugRtId">Object identifier of the plug</param>
    /// <returns></returns>
    Task<PlugConfigurationDto> RegisterPlugAsync(OctoObjectId plugRtId);
    
    /// <summary>
    /// Unregisters a plug from the communication controller
    /// </summary>
    /// <param name="plugRtId">Object identifier of the plug</param>
    /// <returns></returns>
    Task UnRegisterPlugAsync(OctoObjectId plugRtId);
}