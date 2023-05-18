namespace Meshmakers.Octo.Communication.Plugs.Contracts.Data;

public class UpdatedValueMessage
{
    public string PlugId { get; set; } = null!;
    public string Group { get; set; } = null!;
    public string Name { get; set; } = null!;
    public object? Value { get; set; }
    public DateTime PlugReceivedDateTime { get; set; }
    public DateTime? ExternalReceivedDateTime { get; set; }
}