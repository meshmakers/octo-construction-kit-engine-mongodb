using Meshmakers.Octo.Runtime.Contracts.StreamData;

namespace Meshmakers.Octo.Runtime.Engine.CrateDb.Copy;

/// <summary>What a <c>COPY FROM</c> of one slice came to, as far as a repeat is concerned.</summary>
internal enum LoadVerdict
{
    /// <summary>Every row of the slice was loaded.</summary>
    Complete,

    /// <summary>The slice is incomplete for a reason that goes away (memory pressure, a busy or moving shard): load it again.</summary>
    Transient,

    /// <summary>The slice failed for a reason a repeat does not change: fail at once.</summary>
    Permanent
}

/// <summary>
/// Decides whether a failed <c>COPY FROM</c> is worth repeating (AB#7274, concept §7 step 4). Pure: the text of
/// a CrateDB failure and the figures of a <c>RETURN SUMMARY</c> in, a verdict out.
/// </summary>
/// <remarks>
/// Only conditions that CrateDB itself reports as temporary are transient: its memory circuit breaker, a rejected
/// execution (full thread pool queue) and a shard or node that is not available. Everything else — a missing
/// column, an unreadable staging file, a parse error, a credential or access error — fails again the same way and is
/// not repeated. A statement that ran out of its time budget is transient.
/// </remarks>
internal static class CopyFailureClassifier
{
    private static readonly string[] TransientMarkers =
    [
        // Anchored phrases of the breaker texts, never the bare word: a reason can quote a path or a name.
        "CircuitBreakingException", "circuit_breaking_exception", "breaker would use", "configured breaker",
        "EsRejectedExecutionException", "rejected execution",
        "NoShardAvailableActionException", "UnavailableShardsException", "no shard available",
        "shard not available", "shards not available", "NodeNotConnectedException", "node not connected",
        "NodeDisconnectedException", "primary shard is not active"
    ];

    /// <summary>True when the text of a CrateDB failure names one of the transient conditions.</summary>
    public static bool IsTransientText(string? text)
        => !string.IsNullOrEmpty(text) && TransientMarkers.Any(m => text.Contains(m, StringComparison.OrdinalIgnoreCase));

    /// <summary>The verdict for a statement that failed with <paramref name="failure"/>.</summary>
    public static LoadVerdict ClassifyFailure(Exception failure) => failure switch
    {
        StreamDataStatementTimeoutException => LoadVerdict.Transient,
        // The reason only: the message also holds the statement, whose staging path or table name may contain a marker.
        StreamDataCopyStatementException copy => IsTransientText(copy.Reason) ? LoadVerdict.Transient : LoadVerdict.Permanent,
        _ => LoadVerdict.Permanent
    };

    /// <summary>
    /// The verdict for a statement that returned: the totals of its <c>RETURN SUMMARY</c> and the error text of every
    /// summary row that reported errors.
    /// </summary>
    public static LoadVerdict ClassifySummary(long success, long errors, long expected, IReadOnlyCollection<string> errorTexts)
    {
        if (errors == 0)
        {
            // CrateDB does not count a shard request the circuit breaker refused as an error: short with no error is a repeat.
            return success == expected ? LoadVerdict.Complete : success < expected ? LoadVerdict.Transient : LoadVerdict.Permanent;
        }

        return errorTexts.Count > 0 && errorTexts.All(IsTransientText) ? LoadVerdict.Transient : LoadVerdict.Permanent;
    }
}
