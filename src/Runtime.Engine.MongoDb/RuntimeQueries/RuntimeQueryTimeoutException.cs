using System.Globalization;

namespace Meshmakers.Octo.Runtime.Engine.MongoDb.RuntimeQueries;

/// <summary>
///     A runtime-entity query or count exceeded its time limit and was stopped by MongoDB (<c>maxTimeMS</c>, AB#8897).
///     The work on the database has ended; the caller answers it with a coded error instead of a generic one.
/// </summary>
public sealed class RuntimeQueryTimeoutException : TimeoutException
{
    /// <summary>
    ///     Creates the exception.
    /// </summary>
    /// <param name="tenantId">The tenant the query ran in.</param>
    /// <param name="ckTypeId">The CK type (or CK interface) the query read.</param>
    /// <param name="maxTime">The time limit the query exceeded.</param>
    /// <param name="elapsed">How long the query ran until MongoDB stopped it.</param>
    /// <param name="innerException">The exception of the MongoDB driver.</param>
    public RuntimeQueryTimeoutException(string tenantId, string ckTypeId, TimeSpan maxTime, TimeSpan elapsed,
        Exception? innerException)
        : base(string.Format(CultureInfo.InvariantCulture,
            "The runtime query on CK type '{0}' exceeded the time limit of {1} ms and was stopped. Narrow the query " +
            "(filters, paging) or ask an administrator about the limit (RuntimeQuery:MaxTimeMs).",
            ckTypeId, (long)maxTime.TotalMilliseconds), innerException)
    {
        TenantId = tenantId;
        CkTypeId = ckTypeId;
        MaxTime = maxTime;
        Elapsed = elapsed;
    }

    /// <summary>The tenant the query ran in.</summary>
    public string TenantId { get; }

    /// <summary>The CK type (or CK interface) the query read.</summary>
    public string CkTypeId { get; }

    /// <summary>The time limit the query exceeded.</summary>
    public TimeSpan MaxTime { get; }

    /// <summary>How long the query ran until MongoDB stopped it.</summary>
    public TimeSpan Elapsed { get; }
}
