using Meshmakers.Octo.Communication.Plugs.Contracts.DataTransferObjects;

namespace Meshmakers.Octo.Communication.Plugs.Contracts.Hubs;

public interface IPlugPoolHubCallbacks
{
    Task DeployPlugAsync(PlugPoolPlugDto plug);
    Task UndeployPlugAsync(PlugPoolPlugDto plug);
}