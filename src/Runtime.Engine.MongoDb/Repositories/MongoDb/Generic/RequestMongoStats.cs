namespace Meshmakers.Octo.Runtime.Engine.MongoDb.Repositories.MongoDb.Generic;

/// <summary>
/// Aggregate statistics about MongoDB commands executed during the lifetime of a request scope
/// opened via <see cref="MongoCommandObservability.BeginRequestScope"/>. The values are surfaced
/// to the caller — either in a GraphQL <c>extensions.mongoDb</c> block or as REST response
/// headers — so a user can see at a glance how much of their request time MongoDB cost.
/// </summary>
public sealed class RequestMongoStats
{
    private readonly object _lock = new();
    private int _commandCount;
    private double _totalMs;
    private double _slowestMs;
    private string? _slowestCommand;

    /// <summary>Number of non-suppressed MongoDB commands observed during the scope.</summary>
    public int CommandCount => Volatile.Read(ref _commandCount);

    /// <summary>Sum of every observed command's duration, in milliseconds.</summary>
    public double TotalMs
    {
        get
        {
            lock (_lock)
            {
                return _totalMs;
            }
        }
    }

    /// <summary>Duration of the single slowest observed command, in milliseconds.</summary>
    public double SlowestMs
    {
        get
        {
            lock (_lock)
            {
                return _slowestMs;
            }
        }
    }

    /// <summary>Name of the single slowest observed command (e.g. "find", "aggregate"), or null if none.</summary>
    public string? SlowestCommand
    {
        get
        {
            lock (_lock)
            {
                return _slowestCommand;
            }
        }
    }

    internal void Record(string commandName, double durationMs)
    {
        Interlocked.Increment(ref _commandCount);
        lock (_lock)
        {
            _totalMs += durationMs;
            if (durationMs > _slowestMs)
            {
                _slowestMs = durationMs;
                _slowestCommand = commandName;
            }
        }
    }
}
