using Meshmakers.Octo.Communication.Plugs.Contracts.DataTransferObjects;

namespace Meshmakers.Octo.Communication.Plugs.Contracts.Hubs;

public interface IPoolHubCallbacks
{
    Task DeployPlugAsync(string tenantId, PlugPoolPlugDto plugPoolPlug);
    Task UndeployPlugAsync(string tenantId, PlugPoolPlugDto plugPoolPlug);
}