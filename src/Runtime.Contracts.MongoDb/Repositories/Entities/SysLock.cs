using System.Diagnostics;
using Meshmakers.Octo.ConstructionKit.Contracts;
using Meshmakers.Octo.ConstructionKit.Contracts.DataTransferObjects;

namespace Meshmakers.Octo.Runtime.Contracts.MongoDb.Repositories.Entities;

[DebuggerDisplay("{" + nameof(Id) + "}")]
public class SysLock
{
    /// <summary>
    ///     Constructor
    /// </summary>
    public SysLock()
    {
    }

    /// <summary>
    ///     Defines the id of the construction kit model
    /// </summary>
    public string Id { get; init; } = null!;

    /// <summary>
    /// Gets or sets the creation date and time of the lock
    /// </summary>
    public DateTime? CreationDateTime { get; set; }

    /// <summary>
    /// Gets or sets the expiry date and time of the lock.
    /// After this time, the lock is considered stale and can be claimed by another service.
    /// </summary>
    public DateTime? ExpiryDateTime { get; set; }

    /// <summary>
    /// Gets or sets the last heartbeat timestamp.
    /// Used to extend the lock lifetime for long-running operations.
    /// </summary>
    public DateTime? LastHeartbeat { get; set; }

    /// <summary>
    /// Gets or sets the unique owner token of the service instance currently holding the lock.
    /// Heartbeat and release operations filter on this token, so a stale process cannot
    /// extend or delete a lock that has already been claimed by another instance.
    /// </summary>
    public Guid? OwnerToken { get; set; }

    /// <summary>
    /// Gets or sets a human-readable identifier of the lock holder
    /// (e.g. machine name + process id) for diagnostic purposes.
    /// </summary>
    public string? HolderInfo { get; set; }
}
