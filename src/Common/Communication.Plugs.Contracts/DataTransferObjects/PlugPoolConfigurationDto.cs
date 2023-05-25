namespace Meshmakers.Octo.Communication.Plugs.Contracts.DataTransferObjects;

public record PlugPoolConfigurationDto
{
    public IEnumerable<PlugPoolPlugDto> Plugs { get; set; } = null!;
    
    public string BrokerHost { get; set; } = string.Empty;
    public string BrokerVirtualHost { get; set; } = string.Empty;
    public ushort BrokerPort { get; set; } = 5672;
}