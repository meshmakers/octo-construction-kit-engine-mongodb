using System;

namespace Meshmakers.Octo.Backend.DistributedCache;

/// <summary>
///     Implements a channel message
/// </summary>
public class ChannelMessage<TValue> : IChannelMessage<TValue> where TValue : IConvertible
{
    /// <summary>
    ///     Constructor
    /// </summary>
    /// <param name="message">Message of channel</param>
    /// <param name="hasValue">Is there a value</param>
    internal ChannelMessage(TValue? message, bool hasValue)
    {
        Message = message;
        HasValue = hasValue;
    }

    /// <inheritdoc />
    public bool HasValue { get; }

    /// <inheritdoc />
    public TValue? Message { get; }
}
