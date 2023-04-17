using AspNetCore.Identity.Mongo.Stores;
using Meshmakers.Octo.SystematizedData.Persistence.MongoDb;
using Meshmakers.Octo.SystematizedData.Persistence.SystemEntities;
using Microsoft.AspNetCore.Identity;
using MongoDB.Bson;

namespace Meshmakers.Octo.SystematizedData.Persistence.SystemStores;

public class OctoRoleStore : RoleStore<OctoRole, ObjectId>
{
    public OctoRoleStore(ISystemContext context)
        : base(((IRepositoryInternal)context.OctoSystemDatabase).GetCollection<OctoRole>().GetMongoCollection(),
            new IdentityErrorDescriber())
    {
    }
}
