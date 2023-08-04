using System.Collections.Generic;
using Meshmakers.Octo.Common.Shared;
using Persistence.Contracts;

namespace Meshmakers.Octo.SystematizedData.Persistence.DatabaseEntities;

public interface ICkTypeInfo
{
    /// <summary>
    ///     Gets or sets the construction kit id
    /// </summary>
    CkId<CkTypeId> CkId { get; set; }

    /// <summary>
    ///     Defines the scope the type is created by
    /// </summary>
    ScopeIds ScopeId { get; set; }

    /// <summary>
    ///     If true, the type cannot be inherited again
    /// </summary>
    bool IsFinal { get; set; }

    /// <summary>
    ///     If true, the type cannot be instantiated by a runtime entity
    /// </summary>
    bool IsAbstract { get; set; }

    IEnumerable<ICkBaseTypeInfo> BaseTypes { get; set; }
    ICkTypeDirectedAggregations Associations { get; set; }

    /// <summary>
    ///     Gets or sets a list of attributes
    /// </summary>
    ICollection<ICkEntityAttribute> Attributes { get; set; }

    /// <summary>
    ///     Gets or sets a list of text search languages
    /// </summary>
    ICollection<ICkEntityIndex>? TextSearchLanguages { get; set; }

    string Path { get; }
}