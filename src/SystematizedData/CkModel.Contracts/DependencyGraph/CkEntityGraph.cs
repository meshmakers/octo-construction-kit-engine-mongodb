using System.Diagnostics;
using Meshmakers.Octo.Common.Shared;
using Meshmakers.Octo.SystematizedData.CkModel.Contracts.DataTransferObjects;
using Persistence.Contracts;

namespace Meshmakers.Octo.SystematizedData.CkModel.Contracts.DependencyGraph;

/// <summary>
/// Describes 
/// </summary>
[DebuggerDisplay("{" + nameof(Path) + "}")]
// ReSharper disable once ClassNeverInstantiated.Global
public class CkEntityGraph
{
    public CkEntityGraph(CkId<CkTypeId> ckTypeId, bool isAbstract, bool isFinal,
        ICollection<CkGraphTypeInheritance> baseTypes)
    {
        CkTypeId = ckTypeId;
        IsAbstract = isAbstract;
        IsFinal = isFinal;
        BaseTypes = baseTypes;
        Associations = new();
        Attributes = new List<CkEntityAttributeDto>();
    }


    /// <summary>
    ///     Gets or sets the construction kit id
    /// </summary>
    public CkId<CkTypeId> CkTypeId { get; }

    /// <summary>
    ///     If true, the type cannot be inherited again
    /// </summary>
    public bool IsFinal { get; }

    /// <summary>
    ///     If true, the type cannot be instantiated by a runtime entity
    /// </summary>
    public bool IsAbstract { get; }

    /// <summary>
    /// Returns a list of base types of the give construction kit type
    /// </summary>
    public ICollection<CkGraphTypeInheritance> BaseTypes { get; }

    /// <summary>
    /// Returns a list of associations including inherited ones.
    /// </summary>
    public CkGraphDirectedAssociations Associations { get; }


    public ICollection<CkEntityIndexDto>? Indexes { get; set; }

    /// <summary>
    ///     Gets or sets a list of attributes including inherited ones.
    /// </summary>
    public ICollection<CkEntityAttributeDto> Attributes { get; } 

    /// <summary>
    /// Returns a string that describes the inheritance chain
    /// </summary>
    public string Path => CkTypeId + ": " + string.Join("->", BaseTypes.Select(x => x.BaseCkTypeId));
}