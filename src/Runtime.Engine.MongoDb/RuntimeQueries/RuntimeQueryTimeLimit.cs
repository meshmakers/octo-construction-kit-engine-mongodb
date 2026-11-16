namespace Meshmakers.Octo.Runtime.Engine.MongoDb.RuntimeQueries;

/// <summary>
///     Overrides the time limit of runtime-entity queries for one call site (AB#8897). Internal batch paths - CK
///     migration, blueprint install, backfill, exports - wrap their work in a scope instead of switching the limit off
///     globally:
///     <code>
///     using (RuntimeQueryTimeLimit.Disable())
///     {
///         await blueprintService.InstallAsync(...);
///     }
///     </code>
///     The scope holds for the code that runs inside it, across <c>await</c> and for the tasks it starts (it flows with
///     the execution context, <see cref="AsyncLocal{T}" />), and ends when it is disposed: the limit that was in force
///     before applies again. Scopes nest; the innermost wins. Code outside the scope - other requests, other tasks
///     already running - keeps its own limit.
/// </summary>
public static class RuntimeQueryTimeLimit
{
    private static readonly AsyncLocal<Limit?> CurrentLimit = new();

    /// <summary>
    ///     The limit of the innermost scope, or null when no scope is active (the configured
    ///     <see cref="RuntimeQueryOptions.MaxTimeMs" /> applies).
    /// </summary>
    internal static Limit? Current => CurrentLimit.Value;

    /// <summary>
    ///     Runs the runtime-entity queries of the scope with <paramref name="maxTime" /> instead of the configured limit
    ///     (higher or lower).
    /// </summary>
    /// <param name="maxTime">The time limit; must be positive. Use <see cref="Disable" /> for no limit.</param>
    /// <returns>The scope; dispose it to restore the previous limit.</returns>
    public static IDisposable Override(TimeSpan maxTime)
    {
        if (maxTime <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(maxTime), maxTime,
                "The time limit must be positive; use RuntimeQueryTimeLimit.Disable() for no limit.");
        }

        return new Scope(new Limit(maxTime));
    }

    /// <summary>
    ///     Runs the runtime-entity queries of the scope without a time limit.
    /// </summary>
    /// <returns>The scope; dispose it to restore the previous limit.</returns>
    public static IDisposable Disable() => new Scope(new Limit(null));

    /// <summary>
    ///     The limit a scope sets: <see cref="MaxTime" /> null means no limit.
    /// </summary>
    internal sealed record Limit(TimeSpan? MaxTime);

    private sealed class Scope : IDisposable
    {
        private readonly Limit? _previous;
        private bool _disposed;

        public Scope(Limit limit)
        {
            _previous = CurrentLimit.Value;
            CurrentLimit.Value = limit;
        }

        public void Dispose()
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            CurrentLimit.Value = _previous;
        }
    }
}
