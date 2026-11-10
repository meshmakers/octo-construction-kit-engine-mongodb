namespace Meshmakers.Octo.Runtime.Engine.MongoDb.Repositories.MongoDb;

/// <summary>
///     AB#8677: a CK model import or in-place upgrade replaces the installed version of a model. While it runs (it holds
///     the model import lock), no <c>Available</c> version of that model may exist for a moment, so a dependency on it
///     cannot be resolved. Readers wait for the import instead of failing; when the wait runs out, the failure is marked
///     with <see cref="ExceptionDataKey" /> so that callers (tenant setup) can tell it from a real error and retry.
/// </summary>
public static class CkModelImportInProgress
{
    /// <summary>
    ///     Key in <see cref="Exception.Data" /> of an exception that failed because a CK model was being imported at that
    ///     moment. The value is the comma-separated list of the model names being imported. Callers outside the engine
    ///     match the key as a string so that they do not need a newer engine package.
    /// </summary>
    public const string ExceptionDataKey = "Meshmakers.Octo.CkModelImportInProgress";

    /// <summary>How long a reader waits for a running import of a model it needs before it fails.</summary>
    internal static readonly TimeSpan DefaultWaitTimeout = TimeSpan.FromMinutes(2);

    private static readonly TimeSpan PollInterval = TimeSpan.FromMilliseconds(250);

    /// <summary>
    ///     Whether <paramref name="exception" /> (or an exception it wraps) failed because a CK model was being imported.
    /// </summary>
    public static bool IsMarked(Exception? exception)
    {
        for (var current = exception; current != null; current = current.InnerException)
        {
            if (current.Data.Contains(ExceptionDataKey))
            {
                return true;
            }

            if (current is AggregateException aggregate && aggregate.InnerExceptions.Any(IsMarked))
            {
                return true;
            }
        }

        return false;
    }

    internal static void Mark(Exception exception, IEnumerable<string> modelNames)
    {
        exception.Data[ExceptionDataKey] = string.Join(", ", modelNames);
    }

    /// <summary>
    ///     Waits until none of the models selected by <paramref name="isRelevant" /> is being imported any more.
    ///     Returns <c>true</c> when that happened within <paramref name="timeout" />, <c>false</c> when an import of a
    ///     relevant model was still running at the end.
    /// </summary>
    internal static Task<bool> WaitUntilFinishedAsync(ICkMongoDbRepositoryDataSource dataSource,
        Func<string, bool> isRelevant, TimeSpan timeout, CancellationToken cancellationToken = default)
        => WaitUntilAsync(dataSource, isRelevant, DateTime.UtcNow + timeout, cancellationToken);

    /// <summary>
    ///     Waits like <see cref="WaitUntilFinishedAsync(ICkMongoDbRepositoryDataSource, Func{string, bool}, TimeSpan, CancellationToken)" />,
    ///     but against the one <paramref name="deadline" /> of a whole dependency resolve: the first wait starts it, every
    ///     later wait of the same resolve gets only the time that is left (AB#8677). A resolve that meets several models
    ///     being imported therefore waits at most <see cref="DefaultWaitTimeout" /> in total, not that long per lookup.
    /// </summary>
    internal static Task<bool> WaitUntilFinishedAsync(ICkMongoDbRepositoryDataSource dataSource,
        Func<string, bool> isRelevant, CkModelImportWaitDeadline deadline, CancellationToken cancellationToken = default)
        => WaitUntilAsync(dataSource, isRelevant, deadline.Start(DateTime.UtcNow), cancellationToken);

    private static async Task<bool> WaitUntilAsync(ICkMongoDbRepositoryDataSource dataSource,
        Func<string, bool> isRelevant, DateTime deadline, CancellationToken cancellationToken)
    {
        while (true)
        {
            var running = await dataSource.GetModelsBeingImportedAsync(cancellationToken).ConfigureAwait(false);
            if (!running.Any(isRelevant))
            {
                return true;
            }

            if (DateTime.UtcNow >= deadline)
            {
                return false;
            }

            await Task.Delay(PollInterval, cancellationToken).ConfigureAwait(false);
        }
    }
}

/// <summary>
///     AB#8677: the deadline of all waits for running CK model imports during one dependency resolve. It starts with the
///     first wait and is then fixed; thread-safe, because a resolve may look up dependencies concurrently.
/// </summary>
internal sealed class CkModelImportWaitDeadline
{
    private readonly Lock _gate = new();
    private readonly TimeSpan _budget;
    private DateTime? _deadline;

    /// <summary>A deadline <paramref name="budget" /> after the first wait; default <see cref="CkModelImportInProgress.DefaultWaitTimeout" />.</summary>
    public CkModelImportWaitDeadline(TimeSpan? budget = null)
    {
        _budget = budget ?? CkModelImportInProgress.DefaultWaitTimeout;
    }

    /// <summary>The deadline; the first call fixes it at <paramref name="utcNow" /> plus the budget.</summary>
    public DateTime Start(DateTime utcNow)
    {
        lock (_gate)
        {
            _deadline ??= utcNow + _budget;
            return _deadline.Value;
        }
    }
}
