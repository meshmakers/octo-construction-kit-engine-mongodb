using Meshmakers.Octo.Communication.Contracts.DataTransferObjects;

// ReSharper disable UnusedType.Global
// ReSharper disable UnusedMember.Global

namespace Meshmakers.Octo.Communication.Contracts.Hubs;

/// <summary>
/// Interface of the pool hub that is responsible for registering and unregistering pools and managing their state.
/// </summary>
public interface IPoolHub
{
    /// <summary>
    /// Registers a pool at the controller
    /// </summary>
    /// <param name="poolName">The name of the pool</param>
    /// <returns></returns>
    Task<PoolConfigurationDto> RegisterPoolOperatorAsync(string poolName);

    /// <summary>
    /// Unregisters a pool from the controller
    /// </summary>
    /// <param name="poolName">The name of the pool</param>
    /// <returns></returns>
    Task UnregisterPoolOperatorAsync(string poolName);
}