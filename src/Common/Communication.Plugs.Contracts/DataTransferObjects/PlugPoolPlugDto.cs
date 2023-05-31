using Meshmakers.Octo.Common.Shared;
// ReSharper disable ClassNeverInstantiated.Global

namespace Meshmakers.Octo.Communication.Plugs.Contracts.DataTransferObjects;

public record PlugPoolPlugDto
{
    public OctoObjectId PlugPoolRtId { get; set; }
    public string PlugPoolName { get; set; } = null!;
    public OctoObjectId PlugRtId { get; set; }
    public string ImageName { get; set; } = null!;
    public string Version { get; set; } = null!;
}