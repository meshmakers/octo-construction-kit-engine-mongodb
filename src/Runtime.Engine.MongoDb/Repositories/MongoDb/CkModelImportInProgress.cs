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
    internal static async Task<bool> WaitUntilFinishedAsync(ICkMongoDbRepositoryDataSource dataSource,
        Func<string, bool> isRelevant, TimeSpan timeout, CancellationToken cancellationToken = default)
    {
        var deadline = DateTime.UtcNow + timeout;
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
