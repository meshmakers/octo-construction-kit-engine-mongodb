using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using MongoDB.Bson;
using MongoDB.Bson.Serialization.Attributes;

namespace Meshmakers.Octo.SystematizedData.Persistence.DatabaseEntities;

[DebuggerDisplay("{" + nameof(Path) + "}")]
[BsonIgnoreExtraElements]
// ReSharper disable once ClassNeverInstantiated.Global
public class CkTypeInfo
{
    /// <summary>
    ///     Gets or sets the construction kit id
    /// </summary>
    [BsonId]
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

    public IEnumerable<CkBaseTypeInfo> BaseTypes { get; set; }
    public CkTypeDirectedAggregations Associations { get; set; }


    /// <summary>
    ///     Gets or sets a list of attributes
    /// </summary>
    public ICollection<CkEntityAttribute> Attributes { get; set; }

    /// <summary>
    ///     Gets or sets a list of text search languages
    /// </summary>
    public ICollection<CkEntityIndex>? TextSearchLanguages { get; set; }

    public string Path => CkId + ": " + string.Join("->", BaseTypes.Select(x => x.OriginCkId)) ?? "(none)";
}

public class CkTypeDirectedAggregations
{
    public CkTypeAggregations In { get; set; }
    public CkTypeAggregations Out { get; set; }
}

public class CkTypeAggregations
{
    public ICollection<CkEntityAssociation> Owned { get; set; }
    public ICollection<CkEntityAssociation> Inherited { get; set; }
}

// ReSharper disable once ClassNeverInstantiated.Global
public class CkBaseTypeInfo
{
    /// <summary>
    ///     Returns the mongodb ID
    /// </summary>
    [BsonId]
    public ObjectId InheritanceId { get; set; }

    public ScopeIds ScopeId { get; set; }

    public string OriginCkId { get; set; }

    public string TargetCkId { get; set; }

    public int BaseTypeDepthIndex { get; set; }
}
