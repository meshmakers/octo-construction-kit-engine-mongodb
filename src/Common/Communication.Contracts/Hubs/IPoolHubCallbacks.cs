using Meshmakers.Octo.Communication.Contracts.DataTransferObjects;

// ReSharper disable UnusedType.Global
// ReSharper disable UnusedMember.Global

namespace Meshmakers.Octo.Communication.Contracts.Hubs;

/// <summary>
/// Interfaces of callbacks that can be called by the pool hub.
/// </summary>
public interface IPoolHubCallbacks
{
    /// <summary>
    /// Informs the pool that a new communication adapter has to be deployed.
    /// </summary>
    /// <param name="tenantId">Tenant identifier</param>
    /// <param name="poolCommunicationAdapter">Communication adapter data transfer object</param>
    /// <returns></returns>
    Task DeployCommunicationAdapterAsync(string tenantId, PoolCommunicationAdapterDto poolCommunicationAdapter);
    
    /// <summary>
    /// Inform the pool that a communication adapter has to be undeployed.
    /// </summary>
    /// <param name="tenantId">Tenant identifier</param>
    /// <param name="poolCommunicationAdapter">Communication adapter data transfer object</param>
    /// <returns></returns>
    Task UndeployCommunicationAdapterAsync(string tenantId, PoolCommunicationAdapterDto poolCommunicationAdapter);
}