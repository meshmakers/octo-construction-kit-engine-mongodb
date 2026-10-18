namespace Meshmakers.Octo.Runtime.Engine.CrateDb.Copy;

/// <summary>
/// Runs the statements of the staging copy: the <c>COPY</c> itself and the metadata queries and
/// <c>ALTER TABLE</c> / <c>OPTIMIZE</c> statements around it (AB#5967). Implemented by the CrateDB client;
/// a seam so the copy logic can be tested without a database.
/// </summary>
/// <remarks>
/// A statement runs <b>once</b>, under a command timeout of its own: no resilience pipeline, so a transient
/// failure cannot repeat a <c>COPY FROM</c> that already loaded rows. Failures are reported without the
/// driver's exception and without the credential: <see cref="StreamDataCopyStatementException"/>,
/// <see cref="StreamDataStatementTimeoutException"/> or <see cref="OperationCanceledException"/>.
/// </remarks>
internal interface ICrateCopyClient
{
    /// <param name="tenantId">Tenant whose CrateDB connection runs the statement.</param>
    /// <param name="statement">The statement; the client never shows more than its redacted text.</param>
    /// <param name="commandTimeout">Time the statement may run.</param>
    /// <param name="cancellationToken">Cancels the wait for a connection and the statement.</param>
    Task<CrateStatementResult> ExecuteStatementAsync(
        string tenantId, CopyStatement statement, TimeSpan commandTimeout, CancellationToken cancellationToken);
}

/// <summary>What a statement of the staging copy returned.</summary>
/// <param name="AffectedRows">Rows reported for a statement that returns none (<c>COPY TO</c>: the rows written).</param>
/// <param name="Rows">The rows of a statement that returns a result set (physical column name → value).</param>
internal sealed record CrateStatementResult(long AffectedRows, IReadOnlyList<IReadOnlyDictionary<string, object?>> Rows)
{
    public static readonly CrateStatementResult Empty = new(0, []);
}
