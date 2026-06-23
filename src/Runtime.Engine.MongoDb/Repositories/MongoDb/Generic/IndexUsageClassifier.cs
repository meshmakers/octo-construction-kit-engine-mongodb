namespace Meshmakers.Octo.Runtime.Engine.MongoDb.Repositories.MongoDb.Generic;

/// <summary>
/// Classifies a raw <see cref="IndexUsageEntry"/> (with <see cref="IndexUsageStatus.Used"/>
/// as a placeholder default) into one of the four actionable buckets the Stage 3 surface
/// renders. Pure function — no clock reads, no driver calls — so the surface stays
/// deterministic and the collector stays the single time-aware caller.
/// </summary>
public static class IndexUsageClassifier
{
    /// <summary>
    /// Returns the <see cref="IndexUsageStatus"/> for an entry given the configured age and
    /// low-usage thresholds. The ordering of checks matters:
    /// <list type="number">
    ///   <item><see cref="IndexUsageEntry.IsBuiltin"/> wins over everything — <c>_id_</c>
    ///         is always Builtin regardless of usage figures.</item>
    ///   <item>Indexes younger than <paramref name="minAgeDays"/> are always <c>Used</c>:
    ///         not enough signal yet, never flag a fresh index as unused.</item>
    ///   <item>Zero ops over the age window → <c>Unused</c>.</item>
    ///   <item>Non-zero but below the low-usage threshold → <c>LowUsage</c>.</item>
    ///   <item>Otherwise → <c>Used</c>.</item>
    /// </list>
    /// </summary>
    public static IndexUsageStatus Classify(IndexUsageEntry entry, int minAgeDays, long lowUsageOpsThreshold)
    {
        if (entry.IsBuiltin)
        {
            return IndexUsageStatus.Builtin;
        }

        if (entry.AgeDays < minAgeDays)
        {
            // Too young to judge. An index added an hour ago has had no time to be queried;
            // calling it Unused would be a false positive that the operator might act on.
            return IndexUsageStatus.Used;
        }

        if (entry.OpsCount == 0)
        {
            return IndexUsageStatus.Unused;
        }

        if (entry.OpsCount < lowUsageOpsThreshold)
        {
            return IndexUsageStatus.LowUsage;
        }

        return IndexUsageStatus.Used;
    }
}
