using AspNetCore.Identity.Mongo.Stores;
using Meshmakers.Octo.Backend.Persistence.MongoDb;
using Meshmakers.Octo.Backend.Persistence.SystemEntities;
using Microsoft.AspNetCore.Identity;
using MongoDB.Bson;

namespace Meshmakers.Octo.Backend.Persistence.SystemStores;

public class OctoRoleStore : RoleStore<OctoRole, ObjectId>
{
    public OctoRoleStore(ISystemContext context)
        : base(((IRepositoryInternal)context.OctoSystemDatabase).GetCollection<OctoRole>().GetMongoCollection(),
            new IdentityErrorDescriber())
    {
    }
}
