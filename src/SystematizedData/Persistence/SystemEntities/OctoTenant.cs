using MongoDB.Bson.Serialization.Attributes;
using MongoDB.Bson.Serialization.IdGenerators;

// ReSharper disable UnusedMember.Global

namespace Meshmakers.Octo.Backend.Persistence.SystemEntities;

[CollectionName("OctoTenant")]
public class OctoTenant
{
    [BsonId(IdGenerator = typeof(NullIdChecker))]
    public string TenantId { get; set; }

    public string DatabaseName { get; set; }
}
