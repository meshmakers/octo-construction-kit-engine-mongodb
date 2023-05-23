namespace Meshmakers.Octo.Communication.Plugs.Contracts.Configuration;

public record ServerConfiguration
{
    public string Server { get; set; } = null!;
    
    public IReadOnlyCollection<GroupConfiguration> Groups { get; set; } = null!;
}