using System.Diagnostics;
using Meshmakers.Octo.ConstructionKit.Contracts;

namespace Meshmakers.Octo.SystematizedData.Persistence.DatabaseEntities;

[DebuggerDisplay("{" + nameof(Path) + "}")]
// ReSharper disable once ClassNeverInstantiated.Global
public class CkTypeInfo
{
    /// <summary>
    ///     Gets or sets the construction kit id
    /// </summary>
    public CkId<CkTypeId> CkTypeId { get; set; }

    /// <summary>
    ///     If true, the type cannot be inherited again
    /// </summary>
    public bool IsFinal { get; set; }

    /// <summary>
    ///     If true, the type cannot be instantiated by a runtime entity
    /// </summary>
    public bool IsAbstract { get; set; }

    public IEnumerable<CkBaseTypeInfo> BaseTypes { get; set; } = new List<CkBaseTypeInfo>();
    public CkTypeDirectedAggregations Associations { get; set; } = new();

    public ICollection<CkTypeIndex>? Indexes { get; set; }


    /// <summary>
    ///     Gets or sets a list of attributes
    /// </summary>
    public ICollection<CkTypeAttribute> Attributes { get; set; } = new List<CkTypeAttribute>();

    /// <summary>
    ///     Gets or sets a list of text search languages
    /// </summary>
    public ICollection<CkTypeIndex>? TextSearchLanguages { get; set; }

    public string Path => CkTypeId + ": " + string.Join("->", BaseTypes.Select(x => x.OriginCkTypeId));
}

// ReSharper disable once ClassNeverInstantiated.Global