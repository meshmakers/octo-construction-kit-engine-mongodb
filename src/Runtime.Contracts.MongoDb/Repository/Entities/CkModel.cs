using System.Diagnostics;
using Meshmakers.Octo.ConstructionKit.Contracts;

namespace Meshmakers.Octo.Runtime.Contracts.MongoDb.Repository.Entities;

[DebuggerDisplay("{" + nameof(Id) + "}")]
public class CkModel
{
    /// <summary>
    ///     Constructor
    /// </summary>
    public CkModel()
    {
        Dependencies = Array.Empty<CkModelId>();
    }

    /// <summary>
    ///     Defines the name of the construction kit
    /// </summary>
    public CkModelId Id { get; set; }

    /// <summary>
    ///     Defines the scope the type is created by
    /// </summary>
    public ScopeIds ScopeId { get; set; }

    /// <summary>
    ///     Defines the dependencies of the construction kit
    /// </summary>
    public CkModelId[]? Dependencies { get; set; }
}