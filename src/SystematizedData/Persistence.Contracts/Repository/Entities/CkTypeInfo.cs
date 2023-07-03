using System.Diagnostics;

namespace Meshmakers.Octo.SystematizedData.Persistence.DatabaseEntities;

[DebuggerDisplay("{" + nameof(Path) + "}")]
// ReSharper disable once ClassNeverInstantiated.Global
public class CkTypeInfo : ICkTypeInfo
{
    /// <summary>
    ///     Gets or sets the construction kit id
    /// </summary>
    public string CkId { get; set; }

    /// <summary>
    ///     Defines the scope the type is created by
    /// </summary>
    public ScopeIds ScopeId { get; set; }

    /// <summary>
    ///     If true, the type cannot be inherited again
    /// </summary>
    public bool IsFinal { get; set; }

    /// <summary>
    ///     If true, the type cannot be instantiated by a runtime entity
    /// </summary>
    public bool IsAbstract { get; set; }

    public IEnumerable<ICkBaseTypeInfo> BaseTypes { get; set; }
    public ICkTypeDirectedAggregations Associations { get; set; }


    /// <summary>
    ///     Gets or sets a list of attributes
    /// </summary>
    public ICollection<ICkEntityAttribute> Attributes { get; set; }

    /// <summary>
    ///     Gets or sets a list of text search languages
    /// </summary>
    public ICollection<ICkEntityIndex>? TextSearchLanguages { get; set; }

    public string Path => CkId + ": " + string.Join("->", BaseTypes.Select(x => x.OriginCkId)) ?? "(none)";
}

// ReSharper disable once ClassNeverInstantiated.Global