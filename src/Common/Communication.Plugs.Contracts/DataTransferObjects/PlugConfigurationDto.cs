using Meshmakers.Octo.Common.Shared;

namespace Meshmakers.Octo.Communication.Plugs.Contracts.DataTransferObjects;

public record PlugConfigurationDto
{
    public OctoObjectId PlugRtId { get; set; }
    
    public IReadOnlyCollection<ServerConfigurationDto> ServerConfigurations { get; set; } = null!;
}