using Meshmakers.Octo.Communication.Plugs.Contracts.DataTransferObjects;
// ReSharper disable UnusedMember.Global

namespace Meshmakers.Octo.Communication.Plugs.Contracts.Hubs;

public interface IPoolHub
{
    Task<PlugPoolConfigurationDto> RegisterPlugPoolOperatorAsync(string plugPoolName);

    Task UnregisterPlugPoolOperatorAsync(string plugPoolName);
}