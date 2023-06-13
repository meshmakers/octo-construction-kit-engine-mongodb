using Meshmakers.Octo.Communication.Plugs.Contracts.DataTransferObjects;

namespace Meshmakers.Octo.Communication.Plugs.Contracts.Hubs;

public interface IPlugHubCallbacks
{
    Task PlugConfigurationUpdatedAsync(string tenantId, PlugConfigurationDto plugConfiguration);
}