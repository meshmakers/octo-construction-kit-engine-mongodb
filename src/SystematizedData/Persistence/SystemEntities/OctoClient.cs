using Duende.IdentityServer.Models;
using MongoDB.Bson;
using MongoDB.Bson.Serialization.Attributes;

namespace Meshmakers.Octo.SystematizedData.Persistence.SystemEntities;

/// <summary>
///     Represents a client application
/// </summary>
[CollectionName("Clients")]
public class OctoClient : Client
{
    /// <summary>
    ///     Returns the mongodb ID
    /// </summary>
    [BsonId]
    [BsonIgnoreIfDefault]
    public ObjectId Id { get; set; }
}
