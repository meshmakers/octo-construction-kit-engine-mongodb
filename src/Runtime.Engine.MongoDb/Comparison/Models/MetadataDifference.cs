namespace Meshmakers.Octo.Runtime.Engine.MongoDb.Comparison.Models;

/// <summary>
///     Represents a difference in tenant metadata
/// </summary>
public class MetadataDifference
{
    /// <summary>
    ///     Name of the metadata field that differs
    /// </summary>
    public string FieldName { get; set; } = null!;

    /// <summary>
    ///     Value in source tenant
    /// </summary>
    public object? SourceValue { get; set; }

    /// <summary>
    ///     Value in target tenant
    /// </summary>
    public object? TargetValue { get; set; }

    /// <summary>
    ///     Human-readable description of the difference
    /// </summary>
    public string Description { get; set; } = null!;
}
