using AspNetCore.Identity.Mongo.Stores;
using Meshmakers.Octo.Backend.Persistence.MongoDb;
using Meshmakers.Octo.Backend.Persistence.SystemEntities;
using Microsoft.AspNetCore.Identity;
using MongoDB.Bson;

namespace Meshmakers.Octo.Backend.Persistence.SystemStores;

public class OctoUserStore : UserStore<OctoUser, OctoRole, ObjectId>
{
    public OctoUserStore(ISystemContext context)
        : base(((IRepositoryInternal)context.OctoSystemDatabase).GetCollection<OctoUser>().GetMongoCollection(),
            ((IRepositoryInternal)context.OctoSystemDatabase).GetCollection<OctoRole>().GetMongoCollection(),
            new IdentityErrorDescriber())
    {
    }
}
