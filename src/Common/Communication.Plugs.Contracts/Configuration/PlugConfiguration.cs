using Meshmakers.Octo.Common.Shared;

namespace Meshmakers.Octo.Communication.Plugs.Contracts.Configuration;

public record PlugConfiguration
{
    public OctoObjectId PlugId { get; set; }
    
    public IReadOnlyCollection<ServerConfiguration> ServerConfigurations { get; set; } = null!;
}