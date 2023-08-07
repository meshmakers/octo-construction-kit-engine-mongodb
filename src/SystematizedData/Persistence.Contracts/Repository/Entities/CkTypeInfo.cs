using System.Diagnostics;
using Meshmakers.Octo.Common.Shared;
using Persistence.Contracts;

namespace Meshmakers.Octo.SystematizedData.Persistence.DatabaseEntities;

[DebuggerDisplay("{" + nameof(Path) + "}")]
// ReSharper disable once ClassNeverInstantiated.Global
public class CkTypeInfo
{
    /// <summary>
    ///     Gets or sets the construction kit id
    /// </summary>
    public CkId<CkTypeId> CkId { get; set; }

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

    public ICollection<CkEntityIndex>? Indexes { get; set; }


    /// <summary>
    ///     Gets or sets a list of attributes
    /// </summary>
    public ICollection<CkEntityAttribute> Attributes { get; set; } = new List<CkEntityAttribute>();

    /// <summary>
    ///     Gets or sets a list of text search languages
    /// </summary>
    public ICollection<CkEntityIndex>? TextSearchLanguages { get; set; }

    public string Path => CkId + ": " + string.Join("->", BaseTypes.Select(x => x.OriginCkId));
}

// ReSharper disable once ClassNeverInstantiated.Global