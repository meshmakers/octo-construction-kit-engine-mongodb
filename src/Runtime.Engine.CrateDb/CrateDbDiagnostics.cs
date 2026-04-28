using System.Diagnostics;
using System.Diagnostics.Metrics;

namespace Meshmakers.Octo.Runtime.Engine.CrateDb;

/// <summary>
/// CrateDB-side observability surface for the StreamData stack (concept §13). Sits next to the
/// engine-side <c>Meshmakers.Octo.StreamData</c> meter; this meter exposes the data-plane signals
/// that only make sense on the Crate path (insert/query timings, points, required-violations).
/// </summary>
internal static class CrateDbDiagnostics
{
    /// <summary>
    /// Meter name. Subscribers (OpenTelemetry, Prometheus) typically subscribe to both this meter
    /// and the engine's <c>Meshmakers.Octo.StreamData</c> meter to get the full picture.
    /// </summary>
    public const string MeterName = "Meshmakers.Octo.StreamData.Crate";

    /// <summary>
    /// Activity source name for CrateDB-call traces. Spans nest under the engine-side
    /// archive.activate / archive.insert spans when callers wire OpenTelemetry to record both.
    /// </summary>
    public const string ActivitySourceName = MeterName;

    private static readonly Meter Meter = new(MeterName, "1.0.0");

    /// <summary>
    /// Histogram of insert duration in milliseconds (single + bulk). Tags: <c>tenant</c>,
    /// <c>archive</c>, <c>batch_size_bucket</c> (1, 2-10, 11-100, 101-1000, 1000+).
    /// </summary>
    public static readonly Histogram<double> InsertDurationMs =
        Meter.CreateHistogram<double>(
            "streamdata.crate.insert_duration_ms",
            unit: "ms",
            description: "Wall-clock time to insert a stream data point or batch into CrateDB.");

    /// <summary>
    /// Counter of inserted points. Tags: <c>tenant</c>, <c>archive</c>.
    /// </summary>
    public static readonly Counter<long> InsertedPoints =
        Meter.CreateCounter<long>(
            "streamdata.crate.inserted_points",
            unit: "{point}",
            description: "Cumulative count of stream data points written to CrateDB.");

    /// <summary>
    /// Counter of required-attribute violations rejected at insert time. Tags: <c>tenant</c>,
    /// <c>archive</c>, <c>path</c> (the offending CkArchiveColumn.Path).
    /// </summary>
    public static readonly Counter<long> RequiredViolations =
        Meter.CreateCounter<long>(
            "streamdata.crate.required_violations",
            unit: "{violation}",
            description: "Inserts rejected because a required CkArchiveColumn path was missing on the incoming point.");

    /// <summary>
    /// Histogram of query duration in milliseconds. Tags: <c>tenant</c>, <c>archive</c>,
    /// <c>query_type</c> (simple/aggregation/grouped/downsampling).
    /// </summary>
    public static readonly Histogram<double> QueryDurationMs =
        Meter.CreateHistogram<double>(
            "streamdata.crate.query_duration_ms",
            unit: "ms",
            description: "Wall-clock time of a stream data query against CrateDB.");

    /// <summary>
    /// Histogram of rows returned per query. Tags identical to <see cref="QueryDurationMs"/>.
    /// </summary>
    public static readonly Histogram<long> QueryRowsReturned =
        Meter.CreateHistogram<long>(
            "streamdata.crate.query_rows_returned",
            unit: "{row}",
            description: "Row count returned from a stream data query.");

    /// <summary>
    /// Activity source for CrateDB calls. Span names: <c>crate.insert</c>, <c>crate.query</c>,
    /// <c>crate.ddl</c>.
    /// </summary>
    public static readonly ActivitySource ActivitySource = new(ActivitySourceName, "1.0.0");

    /// <summary>
    /// Maps a batch size to a bucket label so cardinality stays bounded.
    /// </summary>
    public static string BatchSizeBucket(int size) => size switch
    {
        <= 1 => "1",
        <= 10 => "2-10",
        <= 100 => "11-100",
        <= 1000 => "101-1000",
        _ => "1000+",
    };
}
