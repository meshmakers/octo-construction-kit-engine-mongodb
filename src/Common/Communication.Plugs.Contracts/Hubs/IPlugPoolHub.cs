using Meshmakers.Octo.Communication.Plugs.Contracts.DataTransferObjects;
// ReSharper disable UnusedMember.Global

namespace Meshmakers.Octo.Communication.Plugs.Contracts.Hubs;

public interface IPlugPoolHub
{
    Task<PlugPoolConfigurationDto> RegisterPlugPoolOperatorAsync(string plugPoolName);
}