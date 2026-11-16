using Meshmakers.Octo.Runtime.Contracts.MongoDb.Repositories.Entities;

namespace Meshmakers.Octo.Runtime.Engine.MongoDb.Repositories.Entities;

public class CkTypeIndexWithName : CkTypeIndex
{
    public required string Name { get; set; }

    /// <summary>
    ///     The index has options the engine never sets on a CK index of its type (collation, sparse, hidden, TTL, or a
    ///     partial filter on a non-unique index): it never counts as the index of a CK definition (AB#8960).
    /// </summary>
    public bool HasForeignOptions { get; set; }
}
