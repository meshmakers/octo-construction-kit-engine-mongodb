namespace Meshmakers.Octo.Runtime.Contracts.MongoDb;

/// <summary>
/// Exception thrown when a session operation fails.
/// </summary>
public class SessionOperationException : OperationFailedException
{
    protected SessionOperationException()
    {
    }

    protected SessionOperationException(string message) : base(message)
    {
    }

    protected SessionOperationException(string message, Exception inner) : base(message, inner)
    {
    }

    internal static Exception SessionAlreadyStarted()
    {
        return new SessionOperationException("Session already started.");
    }

    internal static Exception SessionNotActive()
    {
        return new SessionOperationException("Session not active.");
    }
}