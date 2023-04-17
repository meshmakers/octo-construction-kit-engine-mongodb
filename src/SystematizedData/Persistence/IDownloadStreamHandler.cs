using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Meshmakers.Octo.Common.Shared;

namespace Meshmakers.Octo.Backend.Persistence;

/// <summary>
///     Handles the download of a stream from a persistent storage
/// </summary>
public interface IDownloadStreamHandler : IDisposable
{
    /// <summary>
    ///     Returns the object id of the binary
    /// </summary>
    OctoObjectId Id { get; }

    /// <summary>
    ///     Returns the used content type during upload
    /// </summary>
    string ContentType { get; }


    /// <summary>
    ///     Returns upload date/time
    /// </summary>
    DateTime UploadDateTime { get; }


    /// <summary>
    ///     Returns the stream
    /// </summary>
    Stream Stream { get; }

    /// <summary>
    ///     Returns the file name
    /// </summary>
    string Filename { get; }

    /// <summary>
    ///     Closes the GridFS stream.
    /// </summary>
    /// <param name="cancellationToken">The cancellation token.</param>
    void Close(CancellationToken cancellationToken);

    /// <summary>
    ///     Closes the GridFS stream.
    /// </summary>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>A Task.</returns>
    Task CloseAsync(CancellationToken cancellationToken = default);
}
