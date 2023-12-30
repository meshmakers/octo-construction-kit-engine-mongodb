namespace Meshmakers.Octo.Common.Shared.DataTransferObjects;

/// <summary>
/// Represents the field group by type for graphql
/// </summary>
public class FieldGroupByDto
{
    /// <summary>
    /// Attribute names to group by
    /// </summary>
    public IEnumerable<string> GroupByAttributeNameList { get; set; } = null!;
    
    /// <summary>
    /// Attribute names whose existence to count, NULL values are not counted.
    /// </summary>
    public IEnumerable<string> CountAttributeNameList { get; set; } = null!;
    
    /// <summary>
    /// Attributes names whose maximum value is to be determined.
    /// </summary>
    public IEnumerable<string> MaxValueAttributeNameList { get; set; } = null!;
    
    /// <summary>
    /// Attributes names whose minimum value is to be determined.
    /// </summary>
    public IEnumerable<string> MinValueAttributeNameList { get; set; } = null!;
    
    /// <summary>
    /// Attributes names whose average value is to be determined.
    /// </summary>
    public IEnumerable<string> AvgAttributeNameList { get; set; } = null!;
}