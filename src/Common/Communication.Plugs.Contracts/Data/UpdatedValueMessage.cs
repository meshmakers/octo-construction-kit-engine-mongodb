namespace Meshmakers.Octo.Communication.Plugs.Contracts.Data;

public class UpdatedValueMessage
{
    public string PlugId { get; set; }
    public string Group { get; set; }
    public string Name { get; set; }
    public object? Value { get; set; }
    public DateTime PlugReceivedDateTime { get; set; }
    public DateTime? ExternalReceivedDateTime { get; set; }
}