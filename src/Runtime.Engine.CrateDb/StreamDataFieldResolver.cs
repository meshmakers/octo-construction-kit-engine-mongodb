using Meshmakers.Common.Shared;

namespace Meshmakers.Octo.Runtime.Engine.CrateDb;

/// <summary>
/// Classification of a stream data field.
/// </summary>
public enum StreamDataFieldCategory
{
    /// <summary>A built-in default field (rtId, timestamp, etc.)</summary>
    Default,

    /// <summary>A CK model data stream attribute backed by a typed archive column</summary>
    DataStream,

    /// <summary>Field not recognized as default or data stream</summary>
    Unknown
}

/// <summary>
/// Result of resolving a stream data field name.
/// </summary>
/// <param name="Category">Whether the field is a default, data stream, or unknown field.</param>
/// <param name="CrateDbName">
/// The canonical camelCase CrateDB column name (concept §9, T17). Both default and data-stream
/// fields are first-class typed columns on the per-archive table — there is no longer a generic
/// <c>data</c> blob, so this is always a direct column reference.
/// </param>
/// <param name="GraphQlAlias">The camelCase alias used in GraphQL responses.</param>
public record ResolvedField(
    StreamDataFieldCategory Category,
    string CrateDbName,
    string GraphQlAlias);

/// <summary>
/// Central resolver for stream data field names. Maps input attribute paths (in any casing) to
/// their canonical CrateDB column name and GraphQL alias. Per the T17 hard cut every attribute
/// has its own typed column, so the dotted path <c>sensor.reading.value</c> resolves to column
/// <c>sensorReadingValue</c> via <see cref="ColumnNameMapper"/>; the legacy
/// <c>data['attribute']</c> indirection is gone.
/// </summary>
public class StreamDataFieldResolver
{
    private readonly Dictionary<string, ResolvedField> _fields = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Creates a new resolver with the given data stream attribute paths. Default fields are
    /// always registered from <see cref="Constants.DefaultStreamDataFields"/>.
    /// </summary>
    /// <param name="dataStreamAttributePaths">
    /// Attribute paths from the CK model. May be dotted (e.g. <c>sensor.reading.value</c>); each
    /// path is mapped to its column name via <see cref="ColumnNameMapper.PathToColumnName"/>.
    /// </param>
    public StreamDataFieldResolver(IEnumerable<string> dataStreamAttributePaths)
    {
        // Register default fields from Constants (single source of truth, already camelCase).
        foreach (var defaultField in Constants.DefaultStreamDataFields)
        {
            _fields[defaultField] = new ResolvedField(
                StreamDataFieldCategory.Default,
                defaultField,
                defaultField);
        }

        // Register data stream attributes
        foreach (var path in dataStreamAttributePaths)
        {
            // Don't overwrite defaults if a data attribute happens to share the name
            if (Constants.IsDefaultField(path))
            {
                continue;
            }

            var columnName = ColumnNameMapper.PathToColumnName(path);
            _fields[path] = new ResolvedField(
                StreamDataFieldCategory.DataStream,
                columnName,
                columnName);
        }
    }

    /// <summary>
    /// Creates a resolver with no data stream attributes (only default fields).
    /// </summary>
    public StreamDataFieldResolver() : this([])
    {
    }

    /// <summary>
    /// Resolves a field name to its canonical representation.
    /// Returns null if the field is not recognized as either a default or data stream field.
    /// </summary>
    public ResolvedField? Resolve(string input)
    {
        return _fields.GetValueOrDefault(input);
    }

    /// <summary>
    /// Resolves a field name, falling back to the camelCased path for unknown fields. Use this
    /// for filter and sort paths that may reference attributes the caller didn't pre-declare —
    /// the underlying SQL column either exists on the archive table or the query fails at
    /// execution time, which is the right place to surface "unknown attribute" errors.
    /// </summary>
    public ResolvedField ResolveOrFallback(string input)
    {
        return Resolve(input) ?? new ResolvedField(
            StreamDataFieldCategory.Unknown,
            ColumnNameMapper.PathToColumnName(input),
            input.ToCamelCase());
    }
}
