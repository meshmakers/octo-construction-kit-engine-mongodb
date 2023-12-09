namespace Meshmakers.Octo.Common.DistributedCache;

/// <summary>
///     Represents a cache stream
/// </summary>
public record CacheStream
{
    /// <summary>
    ///     The content type of the stream
    /// </summary>
    public string ContentType { get; init; } = string.Empty;

    /// <summary>
    ///     The stream as byte array
    /// </summary>
    public byte[] Stream { get; init; } = Array.Empty<byte>();
}