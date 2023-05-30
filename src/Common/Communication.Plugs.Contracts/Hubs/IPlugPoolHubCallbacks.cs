using Meshmakers.Octo.Communication.Plugs.Contracts.DataTransferObjects;

namespace Meshmakers.Octo.Communication.Plugs.Contracts.Hubs;

public interface IPlugPoolHubCallbacks
{
    Task AddPlugAsync(PlugPoolPlugDto plug);
    Task RemovePlugAsync(PlugPoolPlugDto plug);
}