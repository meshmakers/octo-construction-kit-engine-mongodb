namespace Meshmakers.Octo.Runtime.Engine.CrateDb;

/// <summary>
/// CrateDB did not store all rows of an archive write, also after one retry of the rows it missed (AB#8679).
/// </summary>
/// <remarks>
/// <para>
/// Under memory pressure CrateDB ends a shard request of a multi-row <c>INSERT</c> when its memory circuit breaker
/// trips (<c>[query] New used memory … would be larger than configured breaker</c>) and answers the statement
/// without an error: the rows of that shard request are neither stored nor reported. A guarded upsert then lacks
/// their <c>RETURNING</c> rows, an unguarded one reports fewer affected rows than it sent. The client retries the
/// missing rows of the statement once — the upsert is idempotent — and throws this exception when rows are still
/// missing.
/// </para>
/// <para>
/// It is a failure of the write, never a precedence decision: callers must not treat the batch as stored or as
/// skipped. Retrying the whole write later is safe for the same reason the client's retry is.
/// </para>
/// </remarks>
public sealed class StreamDataWriteIncompleteException : Exception
{
    internal StreamDataWriteIncompleteException(string qualifiedTable, int rowsSent, int rowsMissing, string detail)
        : base(
            FormattableString.Invariant(
                $"CrateDB did not store {rowsMissing} of {rowsSent} rows written to archive table {qualifiedTable}, also after one retry. {detail} The likely cause is a CrateDB memory circuit breaker that ended a shard request without an error (see the CrateDB log: 'would be larger than configured breaker'). The write failed; nothing of it may be counted as stored or skipped."))
    {
        QualifiedTable = qualifiedTable;
        RowsSent = rowsSent;
        RowsMissing = rowsMissing;
    }

    /// <summary>The archive table the rows were written to (<c>schema.table</c>; the table is named after the archive).</summary>
    public string QualifiedTable { get; }

    /// <summary>The rows of the statement that was retried.</summary>
    public int RowsSent { get; }

    /// <summary>The rows CrateDB did not store after the retry.</summary>
    public int RowsMissing { get; }
}
