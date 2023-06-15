using Meshmakers.Octo.Common.Shared;

namespace Meshmakers.Octo.Communication.Plugs.Contracts.DataTransferObjects;

public record GroupConfigurationDto
{
    public IReadOnlyCollection<MappingConfigurationDto> Mappings { get; set; } = null!;
    public string Name { get; set; } = null!;
    public OctoObjectId Id { get; set; }


    public virtual bool Equals(GroupConfigurationDto? other)
    {
        if (other == null)
            return false;
        var b = Mappings.All(x => other.Mappings.Any(y => y.Equals(x)));
        return Name.Equals(other.Name) && Id.Equals(other.Id) && b;
    }
}