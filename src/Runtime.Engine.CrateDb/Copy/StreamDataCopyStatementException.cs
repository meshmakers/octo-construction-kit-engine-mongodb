namespace Meshmakers.Octo.Runtime.Engine.CrateDb.Copy;

/// <summary>
/// A staging-copy statement (<c>COPY</c>, or one of the statements around it) failed in CrateDB or on the way
/// there. A <c>COPY</c> carries the storage credential in its text and the driver's own exception carries that
/// text in its message, so this exception is built from scratch: the message is scrubbed, the statement is
/// the redacted spelling, and there is <b>no inner exception</b> and no data bag (AB#5967).
/// </summary>
/// <remarks>
/// A statement that ran out of its time budget is a <see cref="StreamDataStatementTimeoutException"/>, a
/// cancelled one an <see cref="OperationCanceledException"/>; both are likewise free of the credential.
/// </remarks>
public sealed class StreamDataCopyStatementException : Exception
{
    private const int MaxStatementCharacters = 400;

    internal StreamDataCopyStatementException(string redactedStatement, string? sqlState, string scrubbedReason)
        : base(BuildMessage(redactedStatement, sqlState, scrubbedReason))
    {
        Statement = redactedStatement;
        SqlState = sqlState;
    }

    /// <summary>The statement with the credential removed.</summary>
    public string Statement { get; }

    /// <summary>The PostgreSQL SQLSTATE of the failure, when CrateDB reported one.</summary>
    public string? SqlState { get; }

    private static string BuildMessage(string redactedStatement, string? sqlState, string scrubbedReason)
    {
        var statement = redactedStatement.Length <= MaxStatementCharacters
            ? redactedStatement
            : redactedStatement[..MaxStatementCharacters] + " …";
        var state = string.IsNullOrEmpty(sqlState) ? string.Empty : $" (SQLSTATE {sqlState})";
        return $"A staging copy statement failed{state}: {scrubbedReason} Statement: {statement}";
    }
}
