using Meshmakers.Octo.Runtime.Contracts.Repositories;

namespace Meshmakers.Octo.Runtime.Engine.MongoDb.RuntimeQueries;

/// <summary>
///     Runs an engine-internal read that loads a whole type on purpose (for example the archive and rollup archive
///     enumerations behind series resolution and the stream-data hosted services, or the recompute jobs of an archive
///     the computed-column backfill drain reads every tick) outside the runtime read guard's
///     attribution (AB#9824): such a read runs inside a node when a node resolves a stream-data series, and must not
///     be reported as that node's unbounded read nor use up its one report per execution. Only the awaited read is
///     suppressed; the caller's scope is restored when it returns, so a node's own reads stay reported.
/// </summary>
internal static class EngineInternalRead
{
    /// <summary>Awaits <paramref name="read" /> inside <see cref="RuntimeReadScope.Suppress" />.</summary>
    public static async Task<TResult> RunAsync<TResult>(Func<Task<TResult>> read)
    {
        using (RuntimeReadScope.Suppress())
        {
            return await read().ConfigureAwait(false);
        }
    }
}
