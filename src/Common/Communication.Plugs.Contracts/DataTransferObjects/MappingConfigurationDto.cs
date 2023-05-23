using Meshmakers.Octo.Common.Shared;

namespace Meshmakers.Octo.Communication.Plugs.Contracts.DataTransferObjects;

public record MappingConfigurationDto
{
    public string Name { get; set; } = null!;
    public OctoObjectId Id { get; set; }
    
    public string Configuration { get; set; } = null!; 
}