using Meshmakers.Octo.Common.Shared;

namespace Meshmakers.Octo.Communication.Plugs.Contracts.DataTransferObjects;

public record PlugConfigurationDto
{
    public OctoObjectId PlugRtId { get; set; }
    
    public IReadOnlyCollection<ServerConfigurationDto> ServerConfigurations { get; set; } = null!;


    public virtual bool Equals(PlugConfigurationDto? other)
    {
        if (other == null)
            return false;
        var b = ServerConfigurations.All(x => other.ServerConfigurations.Any(y=> y.Equals(x)));
        return PlugRtId.Equals(other.PlugRtId) && b;
    }
}