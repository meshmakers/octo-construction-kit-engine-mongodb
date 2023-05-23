using Meshmakers.Octo.Common.Shared;

namespace Meshmakers.Octo.Communication.Plugs.Contracts.DataTransferObjects;

public record GroupConfigurationDto
{
    public IReadOnlyCollection<MappingConfigurationDto> Mappings { get; set; } = null!;
    public string Name { get; set; } = null!;
    public OctoObjectId Id { get; set; }
}