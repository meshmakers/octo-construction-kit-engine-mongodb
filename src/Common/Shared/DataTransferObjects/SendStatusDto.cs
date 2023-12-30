namespace Meshmakers.Octo.Common.Shared.DataTransferObjects;

/// <summary>
///     Represents the status of a send operation.
/// </summary>
public enum SendStatusDto
{
    /// <summary>
    ///     The send operation is pending
    /// </summary>
    Pending = 0,

    /// <summary>
    ///     The send operation was successful
    /// </summary>
    Sent = 1,

    /// <summary>
    ///     The sender operation failed
    /// </summary>
    Error = 2
}