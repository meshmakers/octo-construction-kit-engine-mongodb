namespace Meshmakers.Octo.Communication.Plugs.Contracts.DataTransferObjects;

public record ServerConfigurationDto
{
    public string Server { get; set; } = null!;
    
    public IReadOnlyCollection<GroupConfigurationDto> Groups { get; set; } = null!;
}