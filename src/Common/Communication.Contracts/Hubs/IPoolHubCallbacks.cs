using Meshmakers.Octo.Communication.Contracts.DataTransferObjects;

// ReSharper disable UnusedType.Global
// ReSharper disable UnusedMember.Global

namespace Meshmakers.Octo.Communication.Contracts.Hubs;

/// <summary>
/// Interfaces of callbacks that can be called by the plug pool hub.
/// </summary>
public interface IPoolHubCallbacks
{
    /// <summary>
    /// Informs the plug pool that a new plug has to be deployed.
    /// </summary>
    /// <param name="tenantId">Tenant identifier</param>
    /// <param name="plugPoolPlug">Plug configuration data transfer object</param>
    /// <returns></returns>
    Task DeployPlugAsync(string tenantId, PlugPoolPlugDto plugPoolPlug);
    
    /// <summary>
    /// Inform the plug pool that a plug has to be undeployed.
    /// </summary>
    /// <param name="tenantId">Tenant identifier</param>
    /// <param name="plugPoolPlug">Plug configuration data transfer object</param>
    /// <returns></returns>
    Task UndeployPlugAsync(string tenantId, PlugPoolPlugDto plugPoolPlug);
}