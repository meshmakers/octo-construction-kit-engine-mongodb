using System.Diagnostics;
using Meshmakers.Octo.Common.Shared;

namespace Meshmakers.Octo.SystematizedData.Persistence.DatabaseEntities;

[DebuggerDisplay("{" + nameof(Id) + "}")]
public class CkModel
{
    /// <summary>
    /// Defines the name of the construction kit
    /// </summary>
    public CkModelId? Id { get; set; }
    
    /// <summary>
    ///     Defines the scope the type is created by
    /// </summary>
    public ScopeIds ScopeId { get; set; }

}