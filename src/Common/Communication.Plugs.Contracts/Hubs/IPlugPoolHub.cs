using Meshmakers.Octo.Communication.Plugs.Contracts.DataTransferObjects;

namespace Meshmakers.Octo.Communication.Plugs.Contracts.Hubs;

public interface IPlugPoolHub
{
    Task<PlugPoolConfigurationDto> RegisterPlugPool(string plugPoolName);
}