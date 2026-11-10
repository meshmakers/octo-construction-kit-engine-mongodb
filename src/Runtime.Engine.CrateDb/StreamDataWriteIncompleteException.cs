namespace Meshmakers.Octo.Runtime.Engine.CrateDb;

/// <summary>
/// CrateDB did not store all rows of an archive write (AB#8679, AB#8680): a write that was retried once is still
/// short, or a count check over a whole rollup recompute found rows missing.
/// </summary>
/// <remarks>
/// <para>
/// Under memory pressure CrateDB ends a shard request of a multi-row <c>INSERT</c> when its memory circuit breaker
/// trips (<c>[query] New used memory … would be larger than configured breaker</c>) and answers the statement
/// without an error: the rows of that shard request are neither stored nor reported. A guarded upsert then lacks
/// their <c>RETURNING</c> rows, an unguarded one reports fewer affected rows than it sent. The client retries the
/// missing rows of the statement once — the upsert is idempotent — and throws this exception when rows are still
/// missing (<see cref="AfterRetry" />). A rollup recompute also counts its staging table and its new live
/// generation as a whole before it commits; a shortfall there is thrown without a retry
/// (<see cref="CountCheckFailed" />). The message says which of the two happened.
/// </para>
/// <para>
/// It is a failure of the write, never a precedence decision: callers must not treat the batch as stored or as
/// skipped. Retrying the whole write later is safe for the same reason the client's retry is.
/// </para>
/// </remarks>
public sealed class StreamDataWriteIncompleteException : Exception
{
    private const string BreakerHint =
        "CrateDB memory circuit breaker that ended a shard request without an error (see the CrateDB log: 'would be larger than configured breaker')";

    private const string Consequence = "The write failed; nothing of it may be counted as stored or skipped.";

    private StreamDataWriteIncompleteException(string message, string qualifiedTable, int rowsSent, int rowsMissing)
        : base(message)
    {
        QualifiedTable = qualifiedTable;
        RowsSent = rowsSent;
        RowsMissing = rowsMissing;
    }

    /// <summary>The archive table the rows were written to (<c>schema.table</c>; the table is named after the archive).</summary>
    public string QualifiedTable { get; }

    /// <summary>
    /// The rows that should be in <see cref="QualifiedTable" />: the rows of the statement that was retried, or the
    /// rows a count check expected.
    /// </summary>
    public int RowsSent { get; }

    /// <summary>The rows that are not in <see cref="QualifiedTable" />.</summary>
    public int RowsMissing { get; }

    /// <summary>
    /// A write that CrateDB stored only in part, written once more and still short.
    /// </summary>
    internal static StreamDataWriteIncompleteException AfterRetry(
        string qualifiedTable, int rowsSent, int rowsMissing, string detail)
        => new(
            FormattableString.Invariant(
                $"CrateDB did not store {rowsMissing} of {rowsSent} rows written to archive table {qualifiedTable}, also after one retry. {detail} The likely cause is a {BreakerHint}. {Consequence}"),
            qualifiedTable, rowsSent, rowsMissing);

    /// <summary>
    /// A count check over a whole stage of a write (<paramref name="stage" />, e.g. "staging" or "live copy") found
    /// rows missing. Nothing was retried: the check runs after every statement of the stage reported success.
    /// </summary>
    internal static StreamDataWriteIncompleteException CountCheckFailed(
        string stage, string qualifiedTable, int rowsExpected, int rowsMissing, string detail)
        => new(
            FormattableString.Invariant(
                $"The {stage} count check found {rowsMissing} of {rowsExpected} rows missing in archive table {qualifiedTable}; the check does not retry. {detail} A possible cause is a {BreakerHint}. {Consequence}"),
            qualifiedTable, rowsExpected, rowsMissing);
}
