using Meshmakers.Octo.Common.Shared;

namespace Meshmakers.Octo.Communication.Plugs.Contracts.DataTransferObjects;

public record MappingConfigurationDto
{
    public string Name { get; set; } = null!;
    public OctoObjectId Id { get; set; }
    
    public string Configuration { get; set; } = null!;

    public virtual bool Equals(MappingConfigurationDto? other)
    {
        if (other == null)
            return false;
        return Name.Equals(other.Name) && Id.Equals(other.Id) && Configuration.Equals(other.Configuration);
    }
}