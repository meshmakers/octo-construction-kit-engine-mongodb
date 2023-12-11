namespace Meshmakers.Octo.Common.DistributedCache.Payloads;

/// <summary>
///    Implements an operation payload
/// </summary>
/// <typeparam name="TValue"></typeparam>
// ReSharper disable once ClassNeverInstantiated.Global
public record ChannelOperationResult<TValue> : ChannelMessage
{
    /// <summary>
    /// Constructor
    /// </summary>
    /// <param name="senderClientName">The client name of the sender</param>
    /// <param name="operationId">Operation id, that identifiers the unique call</param>
    /// <param name="result">Return object of the operation invoke</param>
    public ChannelOperationResult(string senderClientName, Guid operationId, TValue result)
        : base(senderClientName)
    {
        OperationId = operationId;
        Result = result;
    }
    
    /// <summary>
    /// Returns the unique id of the operation
    /// </summary>
    public Guid OperationId { get; } = Guid.NewGuid();

    /// <summary>
    /// Returns the result of the operation
    /// </summary>
    public TValue Result { get; init; } = default!;
}