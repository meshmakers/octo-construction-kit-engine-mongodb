using System;
using System.Collections.Generic;
using System.Linq;
using Meshmakers.Octo.Runtime.Contracts.StreamData;

namespace Meshmakers.Octo.Runtime.Contracts.MongoDb;

/// <summary>
/// Thrown by <see cref="ITenantContext.DisableStreamDataAsync"/> while at least one archive of the
/// tenant is still <see cref="CkArchiveStatus.Activated"/> (AB#4255). Disabling stream data is a
/// verified precondition, not a teardown: an activated archive still accepts ingest and is still
/// processed by the rollup/recompute orchestrators, so the flag must not be switched off underneath
/// it. The operator disables the archives (data kept) or deletes them first and retries.
/// </summary>
/// <remarks>
/// <see cref="Exception.Message"/> is a complete, operator-facing sentence naming every blocking
/// archive as <c>Kind 'Name' (Activated)</c> in a deterministic order; hosting services append
/// their own remediation verbs (CLI, MCP, Studio) when they surface it. <see cref="ActivatedArchives"/>
/// carries the same archives structurally for callers that want to render them differently.
/// </remarks>
public sealed class StreamDataDisableBlockedException : StreamDataException
{
    private StreamDataDisableBlockedException(string message, IReadOnlyList<ArchiveSnapshot> activatedArchives)
        : base(message)
    {
        ActivatedArchives = activatedArchives;
    }

    /// <summary>
    /// The archives that were still <see cref="CkArchiveStatus.Activated"/> when the disable was
    /// refused, in the order they are named in <see cref="Exception.Message"/>.
    /// </summary>
    public IReadOnlyList<ArchiveSnapshot> ActivatedArchives { get; }

    /// <summary>
    /// Builds the exception for <paramref name="tenantId"/> from the archives that block the disable.
    /// The archives are ordered by display name, then by runtime id, so the message is stable across
    /// calls regardless of the store's enumeration order.
    /// </summary>
    /// <exception cref="ArgumentException">
    /// <paramref name="activatedArchives"/> is empty - a refusal without a blocking archive is a bug.
    /// </exception>
    public static StreamDataDisableBlockedException Create(string tenantId, IReadOnlyList<ArchiveSnapshot> activatedArchives)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(tenantId);
        ArgumentNullException.ThrowIfNull(activatedArchives);
        if (activatedArchives.Count == 0)
        {
            throw new ArgumentException("At least one activated archive is required.", nameof(activatedArchives));
        }

        var ordered = activatedArchives
            .OrderBy(DisplayName, StringComparer.Ordinal)
            .ThenBy(a => a.RtId.ToString(), StringComparer.Ordinal)
            .ToList();

        var message =
            $"Stream data cannot be disabled for tenant '{tenantId}' while the following archives are still activated: " +
            string.Join(", ", ordered.Select(DescribeArchive)) +
            ". Disable them (data is kept) or delete them - rollups before their source archive - and retry.";

        return new StreamDataDisableBlockedException(message, ordered);
    }

    /// <summary>
    /// Renders one archive as <c>Kind 'Name' (Activated)</c>, where the kind follows the snapshot's
    /// discriminators (<c>RollupArchive</c>, <c>TimeRangeArchive</c>, otherwise <c>RawArchive</c>) and
    /// the name is the well-known name or, when absent, the runtime id.
    /// </summary>
    public static string DescribeArchive(ArchiveSnapshot archive)
    {
        ArgumentNullException.ThrowIfNull(archive);

        var kind = archive.RollupAggregations is not null
            ? "RollupArchive"
            : archive.IsTimeRange
                ? "TimeRangeArchive"
                : "RawArchive";

        return $"{kind} '{DisplayName(archive)}' ({archive.Status})";
    }

    private static string DisplayName(ArchiveSnapshot archive) =>
        string.IsNullOrWhiteSpace(archive.RtWellKnownName) ? archive.RtId.ToString() : archive.RtWellKnownName;
}
