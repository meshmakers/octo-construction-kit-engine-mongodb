using System.Diagnostics;

namespace Meshmakers.Octo.Runtime.Engine.CrateDb;



/// <summary>
/// Exception thrown by the StreamData client.
/// </summary>
public class StreamDataException : Exception
{
    private StreamDataException(string message) : base(message)
    {
        
    }

    internal static Exception CouldNotCreateDatabaseConnection() => new StreamDataException("Could not create database connection");

    [StackTraceHidden]
    internal static Exception ConnectionDisposedException() =>
        new StreamDataException("Connection is already disposed.");

    internal static Exception InvalidQueryParameters(string message) => new StreamDataException(message);
}