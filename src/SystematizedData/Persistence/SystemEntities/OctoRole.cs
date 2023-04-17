using AspNetCore.Identity.Mongo.Model;

namespace Meshmakers.Octo.Backend.Persistence.SystemEntities;

[CollectionName("IdentityRoles")]
public class OctoRole : MongoRole
{
}
