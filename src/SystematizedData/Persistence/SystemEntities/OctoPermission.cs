using System.Collections.Generic;
using MongoDB.Bson;
using MongoDB.Bson.Serialization.Attributes;

namespace Meshmakers.Octo.SystematizedData.Persistence.SystemEntities;

[CollectionName("Permissions")]
public class OctoPermission
{
    /// <summary>
    ///     Returns the mongodb ID
    /// </summary>
    [BsonId]
    [BsonIgnoreIfDefault]
    public ObjectId Id { get; set; }

    public string PermissionId { get; set; }

    public ICollection<string> PermissionRoleIds { get; set; }
}
