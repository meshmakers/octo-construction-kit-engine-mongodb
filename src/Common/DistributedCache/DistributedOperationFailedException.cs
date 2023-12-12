namespace Meshmakers.Octo.Common.DistributedCache;

/// <summary>
/// Distributed operation failed exception
/// </summary>
/// <typeparam name="TError"></typeparam>
public class DistributedOperationFailedException<TError> : Exception
{
    /// <summary>
    /// Returns the error object of distributed operation
    /// </summary>
    public TError Error { get; }

    /// <summary>
    /// Constructor
    /// </summary>
    /// <param name="error">Error object of distributed operation</param>
    public DistributedOperationFailedException(TError error)
    {
        Error = error;
    }

    /// <summary>
    /// Constructor
    /// </summary>
    /// <param name="message">Exception message</param>
    /// <param name="error">Error object of distributed operation</param>
    public DistributedOperationFailedException(string message, TError error) : base(message)
    {
        Error = error;
    }

    /// <summary>
    /// Constructor
    /// </summary>
    /// <param name="message">Exception message</param>
    /// <param name="error">Error object of distributed operation</param>
    /// <param name="inner">Inner exception</param>
    public DistributedOperationFailedException(string message, TError error, Exception inner) : base(message, inner)
    {
        Error = error;
    }
}