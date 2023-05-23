using Meshmakers.Octo.Common.Shared;

namespace Meshmakers.Octo.Communication.Plugs.Contracts.DataTransferObjects;

public record PlugPoolConfigurationDto
{
    public IEnumerable<OctoObjectId> PlugIds { get; set; } = null!;
}