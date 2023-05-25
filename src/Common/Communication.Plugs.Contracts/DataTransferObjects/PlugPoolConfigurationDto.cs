namespace Meshmakers.Octo.Communication.Plugs.Contracts.DataTransferObjects;

public record PlugPoolConfigurationDto
{
    public IEnumerable<PlugPoolPlugDto> Plugs { get; set; } = null!;
    

}