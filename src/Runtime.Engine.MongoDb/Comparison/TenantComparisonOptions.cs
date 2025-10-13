
namespace Meshmakers.Octo.Runtime.Engine.MongoDb.Comparison;

/// <summary>
///     Options for configuring tenant comparison behavior
/// </summary>
public class TenantComparisonOptions
{
    /// <summary>
    ///     Comparison areas to include (metadata, models, entities, associations)
    /// </summary>
    public ComparisonAreas Areas { get; set; } = ComparisonAreas.All;

    /// <summary>
    ///     Maximum number of entities to compare per type (null = no limit).
    ///     Useful for limiting comparison scope on large tenants.
    /// </summary>
    public int? MaxEntitiesPerType { get; set; }

    /// <summary>
    ///     Include detailed property differences in entity comparison
    /// </summary>
    public bool IncludePropertyDifferences { get; set; } = true;

    /// <summary>
    ///     Include association differences in comparison
    /// </summary>
    public bool IncludeAssociationDifferences { get; set; } = true;

    /// <summary>
    ///     The optional strategy to use for matching entities between the two tenants.
    ///     If not specified, the default matching strategy will be used.
    /// </summary>
    public string? ConfigurationKey { get; set; } = null;
}
