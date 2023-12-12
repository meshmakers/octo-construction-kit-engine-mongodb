namespace Meshmakers.Octo.Common.DistributedCache.Payloads;

/// <summary>
///    Implements an operation payload
/// </summary>
/// <typeparam name="TResult">Type of result object</typeparam>
// ReSharper disable once ClassNeverInstantiated.Global
public record ChannelOperationResult<TResult> : ChannelMessage
{
    /// <summary>
    /// Constructor
    /// </summary>
    /// <param name="senderClientName">The client name of the sender</param>
    /// <param name="operationId">Operation id, that identifiers the unique call</param>
    /// <param name="result">Return object of the operation invoke</param>
    /// <param name="error">Error object of the operation invoke</param>
    public ChannelOperationResult(string senderClientName, Guid operationId, TResult? result, OperationError? error)
        : base(senderClientName)
    {
        OperationId = operationId;
        Result = result;
        Error = error;
    }
    
    /// <summary>
    /// Returns the unique id of the operation
    /// </summary>
    public Guid OperationId { get; } = Guid.NewGuid();

    /// <summary>
    /// Returns the result of the operation
    /// </summary>
    public TResult? Result { get; init; }
    
    /// <summary>
    /// Returns the error object of the operation
    /// </summary>
    public OperationError? Error { get; init; }
}