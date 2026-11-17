using Meshmakers.Octo.Runtime.Contracts.MongoDb.Repositories.Entities;

namespace Meshmakers.Octo.Runtime.Engine.MongoDb.Repositories.Entities;

public class CkTypeIndexWithName : CkTypeIndex
{
    public required string Name { get; set; }

    /// <summary>
    ///     The index has options the engine never sets on a CK index of its type (collation, sparse, hidden, TTL, a
    ///     partial filter on a non-unique index, or a unique index without exactly the engine's type (and state) partial
    ///     filter): it never counts as the index of a CK definition (AB#8960).
    /// </summary>
    public bool HasForeignOptions { get; set; }

    /// <summary>
    ///     The type ids the partial filter of a unique index names (<c>ckTypeId: {$in: [...]}</c>), or null without one
    ///     (AB#8960: a unique index with another type set is replaced).
    /// </summary>
    public IReadOnlySet<string>? PartialFilterTypeIds { get; set; }
}
