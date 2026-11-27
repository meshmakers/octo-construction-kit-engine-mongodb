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
///         <c>octo_ck_explicit_import_downgraded_total</c>, <c>octo_ck_explicit_import_refused_total</c> (AB#9325), <c>octo_ck_restore_system_model_refused_total</c> and
///         <c>octo_ck_restore_system_model_raised_total</c> and
///         <c>octo_ck_model_revalidated_total</c> (D-G1-2; with
///         <c>unit: "count"</c> they were exported as <c>…_count_total</c>).
///     </para>
/// </summary>
public static class CkModelImportDiagnostics
{
    /// <summary>Counter: an embedded/startup CK model import was skipped because the tenant has a newer version.</summary>
    public const string EmbeddedImportSkippedCounterName = "octo.ck.embedded_import.skipped";

    /// <summary>Counter: an explicit CK model import replaced a newer installed version (allowed, logged WARN).</summary>
    public const string ExplicitDowngradeCounterName = "octo.ck.explicit_import.downgraded";

    /// <summary>
    ///     Counter: a caller import of a service-managed model (System, System.*) was refused (AB#9325). No <c>model</c>
    ///     tag: the name is caller-supplied, the model id is in the matching WARN line.
    /// </summary>
    public const string ServiceManagedImportRefusedCounterName = "octo.ck.explicit_import.refused";

    /// <summary>
    ///     Counter: a tenant restore was refused before the attach because a service-managed model (System, System.*) of
    ///     its dump is not a genuine published one. Tag <c>reason</c> only (a fixed set, see
    ///     <c>RestoreSystemModelGuard.ReasonTag</c>); no model and no tenant tag: the dump names the model, and the model
    ///     id and the tenant id are in the matching WARN line.
    /// </summary>
    public const string RestoreSystemModelRefusedCounterName = "octo.ck.restore_system_model.refused";

    /// <summary>
    ///     Counter: a tenant restore raised a genuine older service-managed model to the platform's version before the
    ///     attach. Tag <c>model</c>: the name of the raised model, always a published service-managed model.
    /// </summary>
    public const string RestoreSystemModelRaisedCounterName = "octo.ck.restore_system_model.raised";

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

    private static readonly Counter<long> ServiceManagedImportRefused = Meter.CreateCounter<long>(
        ServiceManagedImportRefusedCounterName,
        unit: "{import}",
        description: "Caller imports of a service-managed CK model (System, System.*) that were refused");

    private static readonly Counter<long> RestoreSystemModelRefused = Meter.CreateCounter<long>(
        RestoreSystemModelRefusedCounterName,
        unit: "{model}",
        description: "Service-managed CK models (System, System.*) of a tenant restore that made the restore refuse");

    private static readonly Counter<long> RestoreSystemModelRaised = Meter.CreateCounter<long>(
        RestoreSystemModelRaisedCounterName,
        unit: "{model}",
        description: "Genuine older service-managed CK models a tenant restore raised to the platform's version");

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

    internal static void RecordServiceManagedImportRefused() => ServiceManagedImportRefused.Add(1);

    internal static void RecordRestoreSystemModelRefused(string reason) =>
        RestoreSystemModelRefused.Add(1, new KeyValuePair<string, object?>("reason", reason));

    internal static void RecordRestoreSystemModelRaised(string modelName) =>
        RestoreSystemModelRaised.Add(1, new KeyValuePair<string, object?>("model", modelName));

    internal static void RecordRevalidation(string result) =>
        ModelRevalidated.Add(1, new KeyValuePair<string, object?>("result", result));
}
