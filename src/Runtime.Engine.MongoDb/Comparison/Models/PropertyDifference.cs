namespace Meshmakers.Octo.Runtime.Engine.MongoDb.Comparison.Models;

/// <summary>
///     Represents a difference in a property value between source and target entities
/// </summary>
public class PropertyDifference
{
    /// <summary>
    ///     Name of the property that differs
    /// </summary>
    public string PropertyName { get; set; } = null!;

    /// <summary>
    ///     Type of difference (Added, Removed, Modified)
    /// </summary>
    public DifferenceType DifferenceType { get; set; }

    /// <summary>
    ///     Value in source entity (null if property doesn't exist in source)
    /// </summary>
    public object? SourceValue { get; set; }

    /// <summary>
    ///     Value in target entity (null if property doesn't exist in target)
    /// </summary>
    public object? TargetValue { get; set; }
}
