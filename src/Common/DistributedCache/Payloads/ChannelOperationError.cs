namespace Meshmakers.Octo.Common.DistributedCache.Payloads;

/// <summary>
///    Implements an operation error payload
/// </summary>
/// <typeparam name="TError"></typeparam>
// ReSharper disable once ClassNeverInstantiated.Global
public record ChannelOperationError<TError> : ChannelMessage
{
    /// <summary>
    /// Constructor
    /// </summary>
    /// <param name="senderClientName">The client name of the sender</param>
    /// <param name="operationId">Operation id, that identifiers the unique call</param>
    /// <param name="error">Error object of the operation invoke</param>
    public ChannelOperationError(string senderClientName, Guid operationId, TError error)
        : base(senderClientName)
    {
        OperationId = operationId;
        Error = error;
    }
    
    /// <summary>
    /// Returns the unique id of the operation
    /// </summary>
    public Guid OperationId { get; } = Guid.NewGuid();

    /// <summary>
    /// Returns the error result of the operation
    /// </summary>
    public TError Error { get; init; } = default!;
}