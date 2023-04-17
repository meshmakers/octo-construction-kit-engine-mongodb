using System.Collections.Generic;
using MongoDB.Bson;
using MongoDB.Bson.Serialization.Attributes;

namespace Meshmakers.Octo.SystematizedData.Persistence.SystemEntities;

[CollectionName("PermissionRoles")]
public class OctoPermissionRole
{
    /// <summary>
    ///     Returns the mongodb ID
    /// </summary>
    [BsonId]
    [BsonIgnoreIfDefault]
    public ObjectId Id { get; set; }

    public string RoleId { get; set; }

    public string Name { get; set; }

    public ICollection<string> SubjectIds { get; set; }
    public ICollection<string> IdentityRoleIds { get; set; }
}
