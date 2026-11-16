// ReSharper disable UnusedAutoPropertyAccessor.Global
// ReSharper disable AutoPropertyCanBeMadeGetOnly.Global

namespace Meshmakers.Octo.Runtime.Engine.MongoDb.RuntimeQueries;

/// <summary>
///     The server-side time limit of runtime-entity queries (AB#8897, Feature AB#8793). Bound from the host's
///     <c>"RuntimeQuery"</c> configuration section (<c>RuntimeQuery:MaxTimeMs</c>). The limit applies only in a host that
///     registers it with <c>AddRuntimeQueryTimeLimit</c>; in every other host runtime queries run without a limit, as
///     before.
/// </summary>
/// <remarks>
///     The limit is sent to MongoDB as <c>maxTimeMS</c> with every runtime-entity query and count: queries by type, by
///     id, by type with navigation (graph), the counts of these queries and the queries by CK interface. A query that
///     exceeds it is stopped on the server and ends with <see cref="RuntimeQueryTimeoutException" />; a query that
///     completes after more than <see cref="SlowQueryWarningRatio" /> of the limit is logged as a warning. Batch paths
///     override the limit at the call site with <see cref="RuntimeQueryTimeLimit" />.
/// </remarks>
public sealed class RuntimeQueryOptions
{
    /// <summary>
    ///     The configuration section name.
    /// </summary>
    public const string SectionName = "RuntimeQuery";

    /// <summary>
    ///     The default of <see cref="MaxTimeMs" />: 120 s (decision Markus + Odin, 2026-11-12, AB#8793).
    /// </summary>
    public const int DefaultMaxTimeMs = 120_000;

    /// <summary>
    ///     The share of the limit above which a query that completed is logged as a warning (80 %).
    /// </summary>
    public const double SlowQueryWarningRatio = 0.8;

    /// <summary>
    ///     The time limit of one runtime-entity query or count in milliseconds, sent to MongoDB as <c>maxTimeMS</c>.
    ///     Default <see cref="DefaultMaxTimeMs" />; 0 (or a negative value) switches the limit off.
    /// </summary>
    public int MaxTimeMs { get; set; } = DefaultMaxTimeMs;

    /// <summary>
    ///     <see cref="MaxTimeMs" /> as a time span, or null when the limit is off.
    /// </summary>
    internal TimeSpan? MaxTime => MaxTimeMs > 0 ? TimeSpan.FromMilliseconds(MaxTimeMs) : null;
}
