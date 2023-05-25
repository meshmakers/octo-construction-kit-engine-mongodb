namespace Meshmakers.Octo.Communication.Plugs.Contracts.DataTransferObjects;

public record PlugPoolConfigurationDto
{
    public IEnumerable<PlugPoolPlug> Plugs { get; set; } = null!;
}