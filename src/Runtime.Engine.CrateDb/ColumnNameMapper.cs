namespace Meshmakers.Octo.Runtime.Engine.CrateDb;

/// <summary>
/// Maps a CK attribute path (e.g. <c>sensor.reading.value</c>) to its CrateDB column name.
/// Storage names are fully lower-cased after stripping the dot separator — CrateDB's case
/// preservation for quoted identifiers has known quirks in some contexts (notably the
/// <c>EXCLUDED.&quot;Col&quot;</c> reference inside an <c>ON CONFLICT DO UPDATE</c> clause and a
/// few corners of <c>information_schema</c>), so we sidestep the issue by storing every column
/// in canonical lower-case form. The API surface (CkArchive.Columns Path values, GraphQL field
/// projections, query DSL paths) still carries the original PascalCase / dotted form — only the
/// physical CrateDB column is lower-case.
/// </summary>
internal static class ColumnNameMapper
{
    /// <summary>
    /// Converts an attribute path into a CrateDB column name. Segments are concatenated and the
    /// whole result is lower-cased. Examples:
    /// <list type="bullet">
    ///   <item><c>Voltage</c> → <c>voltage</c></item>
    ///   <item><c>CO2Level</c> → <c>co2level</c></item>
    ///   <item><c>sensor.reading.value</c> → <c>sensorreadingvalue</c></item>
    ///   <item><c>Sensor.URL</c> → <c>sensorurl</c></item>
    /// </list>
    /// </summary>
    /// <exception cref="ArgumentException">Thrown when the path is empty or contains an empty segment.</exception>
    public static string PathToColumnName(string path)
    {
        if (string.IsNullOrEmpty(path))
        {
            throw new ArgumentException("Path must not be empty.", nameof(path));
        }

        var segments = path.Split('.');
        var sb = new System.Text.StringBuilder(path.Length);
        foreach (var segment in segments)
        {
            if (segment.Length == 0)
            {
                throw new ArgumentException($"Path '{path}' contains an empty segment.", nameof(path));
            }
            sb.Append(segment);
        }

        return sb.ToString().ToLowerInvariant();
    }
}
