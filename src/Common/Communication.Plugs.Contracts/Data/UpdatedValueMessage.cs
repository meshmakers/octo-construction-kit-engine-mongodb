using Meshmakers.Octo.Common.Shared;

namespace Meshmakers.Octo.Communication.Plugs.Contracts.Data;

public record UpdatedValueMessage
{
    public string TenantId { get; set; } = null!;
    public OctoObjectId PlugId { get; set; }
    public string Group { get; set; } = null!;
    public OctoObjectId MappingId { get; set; }
    public object? Value { get; set; }
    public DateTime PlugReceivedDateTime { get; set; }
    public DateTime? ExternalReceivedDateTime { get; set; }
}