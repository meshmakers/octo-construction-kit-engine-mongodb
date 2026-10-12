using System;
using System.Collections.Generic;
using Microsoft.Extensions.Logging;

namespace Meshmakers.Octo.Runtime.Engine.UnitTests;

/// <summary>A logger that keeps what was logged, for tests that assert on a log line.</summary>
internal sealed class RecordingLogger : ILogger
{
    public List<(LogLevel Level, string Message, Exception? Exception)> Entries { get; } = new();

    public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

    public bool IsEnabled(LogLevel logLevel) => true;

    public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
        Func<TState, Exception?, string> formatter)
    {
        lock (Entries)
        {
            Entries.Add((logLevel, formatter(state, exception), exception));
        }
    }
}
