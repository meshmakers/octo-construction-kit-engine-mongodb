using AspNetCore.Identity.Mongo.Model;

namespace Meshmakers.Octo.SystematizedData.Persistence.SystemEntities;

[CollectionName("IdentityRoles")]
public class OctoRole : MongoRole
{
}
