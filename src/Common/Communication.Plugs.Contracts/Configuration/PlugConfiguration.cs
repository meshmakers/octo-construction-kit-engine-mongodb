using System.Collections.Generic;

namespace Meshmakers.Octo.Communication.Plugs.Contracts.Configuration;

public class PlugConfiguration
{
    public string PlugId { get; set; } = null!;
    
    public IReadOnlyCollection<ServerConfiguration> ServerConfigurations { get; set; } = null!;
}