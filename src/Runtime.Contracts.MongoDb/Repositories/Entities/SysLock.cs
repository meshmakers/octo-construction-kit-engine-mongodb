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
    /// Gets or sets the id of the owner of the lock
    /// </summary>
    public DateTime? CreationDateTime { get; set; }
}
