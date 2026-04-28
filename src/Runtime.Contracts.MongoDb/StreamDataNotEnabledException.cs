using Meshmakers.Octo.Runtime.Contracts.StreamData;

namespace Meshmakers.Octo.Runtime.Contracts.MongoDb;

/// <summary>
/// Thrown when a StreamData operation is requested but the feature is disabled at the instance or
/// tenant level (concept §5 / §12). Distinct from <see cref="ArchiveNotActivatedException"/>,
/// which signals an archive-level state mismatch.
/// </summary>
public sealed class StreamDataNotEnabledException : StreamDataException
{
    /// <summary>
    /// Creates a new <see cref="StreamDataNotEnabledException"/> with the given message.
    /// </summary>
    public StreamDataNotEnabledException(string message) : base(message)
    {
    }
}
