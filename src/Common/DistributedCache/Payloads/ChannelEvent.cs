using System.Text.Json.Serialization;

namespace Meshmakers.Octo.Common.DistributedCache.Payloads;

/// <summary>
///     Implements a channel message
/// </summary>
internal record ChannelEvent<TValue> : ChannelMessage
{
    /// <summary>
    ///     Constructor
    /// </summary>
    /// <param name="senderClientName">The client name of the sender</param>
    /// <param name="message">Message of channel</param>
    [JsonConstructor]
    public ChannelEvent(string senderClientName, TValue? message)
      : base (senderClientName)
    {
        Message = message;
    }

    /// <inheritdoc />
    public TValue? Message { get; init; }
}