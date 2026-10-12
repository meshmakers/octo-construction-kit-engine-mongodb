namespace Meshmakers.Octo.Runtime.Engine.CrateDb;

/// <summary>
/// A CrateDB statement was ended because it ran longer than the time its statement class allows.
/// </summary>
/// <remarks>
/// When the budget ends the driver cancels the statement and CrateDB answers
/// <c>XX000: Job killed</c> — the same answer it gives to any other cancellation. This exception
/// names the one case "the statement was too slow"; a cancellation by the caller surfaces as
/// <see cref="OperationCanceledException"/>. It is not a connection failure: the same statement
/// under the same load will exceed the budget again, so callers must not treat it as transient.
/// </remarks>
public sealed class StreamDataStatementTimeoutException : Exception
{
    internal StreamDataStatementTimeoutException(
        CrateStatementClass statementClass, TimeSpan budget, TimeSpan elapsed, string statement, Exception innerException)
        : base(
            FormattableString.Invariant(
                $"A {statementClass} statement was ended after {elapsed.TotalSeconds:0.0} s; its budget is {budget.TotalSeconds:0.0} s. Statement: {statement}"),
            innerException)
    {
        StatementClass = statementClass;
        Budget = budget;
        Elapsed = elapsed;
    }

    /// <summary>The class of the statement, which decides its budget.</summary>
    public CrateStatementClass StatementClass { get; }

    /// <summary>The time the statement was allowed to run.</summary>
    public TimeSpan Budget { get; }

    /// <summary>The time the statement had run when it was ended.</summary>
    public TimeSpan Elapsed { get; }
}
