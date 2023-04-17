using Duende.IdentityServer.Models;
using MongoDB.Bson;
using MongoDB.Bson.Serialization.Attributes;

namespace Meshmakers.Octo.SystematizedData.Persistence.SystemEntities;

[CollectionName("ApiScopes")]
public class OctoApiScope : ApiScope
{
    /// <summary>
    ///     Returns the mongodb ID
    /// </summary>
    [BsonId]
    [BsonIgnoreIfDefault]
    public ObjectId Id { get; set; }
}
