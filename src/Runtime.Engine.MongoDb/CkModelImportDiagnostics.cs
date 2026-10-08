using System.Diagnostics.Metrics;

using Meshmakers.Octo.Runtime.Engine.MongoDb.Repositories.MongoDb.Generic;

namespace Meshmakers.Octo.Runtime.Engine.MongoDb;

/// <summary>
///     Counters of the CK model import guard and the post-import re-validation (CK v2 F1.0, AB#5900 / AB#5901).
///     They are published on the existing meter <see cref="MongoCommandObservability.MeterName" />
///     (<c>Meshmakers.Octo.MongoDb</c>), which every service that calls <c>AddObservability()</c> already exports.
///     There is deliberately no tenant tag (cardinality); the tenant id is in the matching log line.
///     <para>
///         Units are curly-brace annotations (like <c>CrateDbDiagnostics</c>), which the Prometheus exporter does not
///         append to the name: the series are <c>octo_ck_embedded_import_skipped_total</c>,
///         <c>octo_ck_explicit_import_downgraded_total</c> and <c>octo_ck_model_revalidated_total</c> (D-G1-2; with
///         <c>unit: "count"</c> they were exported as <c>…_count_total</c>).
///     </para>
/// </summary>
public static class CkModelImportDiagnostics
{
    /// <summary>Counter: an embedded/startup CK model import was skipped because the tenant has a newer version.</summary>
    public const string EmbeddedImportSkippedCounterName = "octo.ck.embedded_import.skipped";

    /// <summary>Counter: an explicit CK model import replaced a newer installed version (allowed, logged WARN).</summary>
    public const string ExplicitDowngradeCounterName = "octo.ck.explicit_import.downgraded";

    /// <summary>Counter: outcome of the re-validation of a <c>ResolveFailed</c> model after an import.</summary>
    public const string ModelRevalidatedCounterName = "octo.ck.model.revalidated";

    /// <summary>Tag <c>reason</c>: the installed version is newer within the same major version.</summary>
    public const string ReasonNewerInstalled = "newer_installed";

    /// <summary>Tag <c>reason</c>: the installed version has a higher major version than the embedded one.</summary>
    public const string ReasonNewerMajorInstalled = "newer_major_installed";

    /// <summary>Tag <c>result</c>: a <c>ResolveFailed</c> model resolves again and became <c>Available</c>.</summary>
    public const string ResultRecovered = "recovered";

    /// <summary>Tag <c>result</c>: a <c>ResolveFailed</c> model still does not resolve.</summary>
    public const string ResultStillFailed = "still_failed";

    private static readonly Meter Meter = new(MongoCommandObservability.MeterName, "1.0.0");

    private static readonly Counter<long> EmbeddedImportSkipped = Meter.CreateCounter<long>(
        EmbeddedImportSkippedCounterName,
        unit: "{import}",
        description: "Embedded CK model imports skipped because the tenant already has a newer version (downgrade prevented)");

    private static readonly Counter<long> ExplicitDowngrade = Meter.CreateCounter<long>(
        ExplicitDowngradeCounterName,
        unit: "{import}",
        description: "Explicit CK model imports that replaced a newer installed version");

    private static readonly Counter<long> ModelRevalidated = Meter.CreateCounter<long>(
        ModelRevalidatedCounterName,
        unit: "{model}",
        description: "Re-validation outcomes of ResolveFailed CK models after a CK model import");

    internal static void RecordEmbeddedImportSkipped(string modelName, string reason) =>
        EmbeddedImportSkipped.Add(1,
            new KeyValuePair<string, object?>("model", modelName),
            new KeyValuePair<string, object?>("reason", reason));

    internal static void RecordExplicitDowngrade(string modelName) =>
        ExplicitDowngrade.Add(1, new KeyValuePair<string, object?>("model", modelName));

    internal static void RecordRevalidation(string result) =>
        ModelRevalidated.Add(1, new KeyValuePair<string, object?>("result", result));
}
