using System.Collections.Generic;

namespace Meshmakers.Octo.Communication.Plugs.Contracts.Configuration;

public class ServerConfiguration
{
    public string Server { get; set; } = null!;
    
    public IReadOnlyCollection<GroupConfiguration> Groups { get; set; } = null!;
}