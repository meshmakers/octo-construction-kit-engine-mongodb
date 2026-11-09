using Meshmakers.Octo.Runtime.Contracts.StreamData;
using Microsoft.Extensions.Logging;

namespace Meshmakers.Octo.Runtime.Engine.CrateDb.Client;

/// <summary>
/// The retry of the rows CrateDB did not write in one archive write statement (AB#8679): they are written once more,
/// and when rows are still missing the write fails with <see cref="StreamDataWriteIncompleteException" />.
/// </summary>
/// <remarks>
/// CrateDB ends a shard request of a multi-row <c>INSERT</c> when its memory circuit breaker trips and answers the
/// statement without an error. A guarded statement then lacks the <c>RETURNING</c> rows of the dropped rows, an unguarded
/// one reports fewer affected rows than it sent. Both statements are idempotent upserts: a guarded one decides again on
/// the merged row and answers the same for a row it did store, an unguarded one writes the same values again. The
/// unguarded count does not say which rows are missing, so its retry repeats the whole statement.
/// </remarks>
internal static class MissingRowRetry
{
    /// <summary>What one statement did with its rows; both lists hold positions of the caller's rows.</summary>
    /// <param name="Skipped">Rows the conflict guard kept the stored values for, with the reason.</param>
    /// <param name="Missing">
    /// Rows CrateDB did not write: a guarded statement's rows without a <c>RETURNING</c> row, or for an unguarded one as
    /// many rows as the affected-row count falls short.
    /// </param>
    internal sealed record StatementOutcome(IReadOnlyList<SkippedArchiveRow> Skipped, IReadOnlyList<int> Missing);

    /// <summary>
    /// Runs <paramref name="execute" /> over <paramref name="batch" />, writes the missing rows once more and returns the
    /// rows the guard skipped.
    /// </summary>
    /// <param name="batch">The rows of the statement, as positions of the caller's rows.</param>
    /// <param name="guarded">Whether the statement carries a conflict guard (and <c>RETURNING</c>).</param>
    /// <param name="qualifiedTable">The archive table, named in the log and in the failure.</param>
    /// <param name="execute">Runs one statement over the given rows.</param>
    /// <param name="logger">Logs the retry.</param>
    /// <exception cref="StreamDataWriteIncompleteException">Rows are still missing after the retry.</exception>
    public static async Task<IReadOnlyList<SkippedArchiveRow>> WriteAsync(
        int[] batch, bool guarded, string qualifiedTable, Func<int[], Task<StatementOutcome>> execute, ILogger logger)
    {
        var first = await execute(batch);
        if (first.Missing.Count == 0)
        {
            return first.Skipped;
        }

        var retryRows = guarded ? first.Missing.ToArray() : batch;
        logger.LogWarning(
            "CrateDB did not store {Missing} of {Rows} rows of a write to {Table} without reporting an error " +
            "(likely a memory circuit breaker); writing them once more.",
            first.Missing.Count, batch.Length, qualifiedTable);
        var retry = await execute(retryRows);
        if (retry.Missing.Count > 0)
        {
            throw new StreamDataWriteIncompleteException(qualifiedTable,
                retryRows.Length, retry.Missing.Count,
                guarded
                    ? $"{retry.Missing.Count} rows had no RETURNING row."
                    : $"CrateDB reported {retryRows.Length - retry.Missing.Count} affected rows for {retryRows.Length} rows sent.");
        }

        return guarded ? first.Skipped.Concat(retry.Skipped).OrderBy(r => r.Index).ToList() : retry.Skipped;
    }
}
