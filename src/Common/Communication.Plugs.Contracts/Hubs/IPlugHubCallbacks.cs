using Meshmakers.Octo.Communication.Plugs.Contracts.DataTransferObjects;
// ReSharper disable UnusedType.Global
// ReSharper disable UnusedMember.Global

namespace Meshmakers.Octo.Communication.Plugs.Contracts.Hubs;

/// <summary>
/// Interfaces of callbacks that can be called by the plug hub.
/// </summary>
public interface IPlugHubCallbacks
{
    /// <summary>
    /// Informs a plug that its configuration has been updated.
    /// </summary>
    /// <param name="tenantId">Tenant identifier</param>
    /// <param name="plugConfiguration">The plug configuration data transfer object</param>
    /// <returns></returns>
    Task PlugConfigurationUpdatedAsync(string tenantId, PlugConfigurationDto plugConfiguration);
}