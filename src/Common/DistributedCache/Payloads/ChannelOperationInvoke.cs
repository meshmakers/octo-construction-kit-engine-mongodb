namespace Meshmakers.Octo.Common.DistributedCache.Payloads;

/// <summary>
///    Implements an operation invoke payload
/// </summary>
/// <typeparam name="TValue"></typeparam>
// ReSharper disable once ClassNeverInstantiated.Global
public record ChannelOperationInvoke<TValue> : ChannelMessage
{
    /// <summary>
    /// Constructor
    /// </summary>
    /// <param name="senderClientName">The client name of the sender</param>
    /// <param name="operationId">Operation id, that identifiers the unique call</param>
    /// <param name="arguments">Argument object of the operation invoke</param>
    public ChannelOperationInvoke(string senderClientName, Guid operationId, TValue arguments)
        : base(senderClientName)
    {
        OperationId = operationId;
        Arguments = arguments;
    }
    
    /// <summary>
    /// Returns the unique id of the operation
    /// </summary>
    public Guid OperationId { get; } = Guid.NewGuid();

    /// <summary>
    /// Returns the invoke arguments of the operation
    /// </summary>
    public TValue Arguments { get; init; } = default!;
}