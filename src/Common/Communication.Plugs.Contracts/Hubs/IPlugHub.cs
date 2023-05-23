using Meshmakers.Octo.Common.Shared;
using Meshmakers.Octo.Communication.Plugs.Contracts.DataTransferObjects;

namespace Meshmakers.Octo.Communication.Plugs.Contracts.Hubs;

public interface IPlugHub
{
    Task<PlugConfigurationDto> RegisterPlug(OctoObjectId plugId);
}