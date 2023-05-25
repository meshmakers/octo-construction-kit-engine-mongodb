using Meshmakers.Octo.Common.Shared;

namespace Meshmakers.Octo.Communication.Plugs.Contracts.DataTransferObjects;

public record PlugPoolPlugDto
{
    public OctoObjectId PlugId { get; set; }
    public string ImageName { get; set; }
    public string Version { get; set; }
}