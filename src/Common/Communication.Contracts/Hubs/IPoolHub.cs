using Meshmakers.Octo.Communication.Contracts.DataTransferObjects;

// ReSharper disable UnusedType.Global
// ReSharper disable UnusedMember.Global

namespace Meshmakers.Octo.Communication.Contracts.Hubs;

/// <summary>
/// Interface of the plug pool hub that is responsible for registering and unregistering plug pools and managing their state.
/// </summary>
public interface IPoolHub
{
    /// <summary>
    /// Registers a plug pool at the plug controller
    /// </summary>
    /// <param name="plugPoolName">The name of the plug</param>
    /// <returns></returns>
    Task<PoolConfigurationDto> RegisterPlugPoolOperatorAsync(string plugPoolName);

    /// <summary>
    /// Unregisters a plug pool from the plug controller
    /// </summary>
    /// <param name="plugPoolName">The name of the plug</param>
    /// <returns></returns>
    Task UnregisterPlugPoolOperatorAsync(string plugPoolName);
}