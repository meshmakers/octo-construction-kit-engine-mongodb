using Duende.IdentityServer.Models;
using MongoDB.Bson;
using MongoDB.Bson.Serialization.Attributes;

namespace Meshmakers.Octo.SystematizedData.Persistence.SystemEntities;

[CollectionName("IdentityResources")]
public class OctoIdentityResource : IdentityResource
{
    /// <summary>
    ///     Returns the mongodb ID
    /// </summary>
    [BsonId]
    [BsonIgnoreIfDefault]
    public ObjectId Id { get; set; }
}
