using Meshmakers.Octo.Common.Shared;

namespace Meshmakers.Octo.Communication.Plugs.Contracts.DataTransferObjects;

public record PlugPoolPlug
{
    public OctoObjectId PlugId { get; set; }
    public string Version { get; set; }
}