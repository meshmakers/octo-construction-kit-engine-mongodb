using AspNetCore.Identity.Mongo.Stores;
using Meshmakers.Octo.SystematizedData.Persistence.MongoDb;
using Meshmakers.Octo.SystematizedData.Persistence.SystemEntities;
using Microsoft.AspNetCore.Identity;
using MongoDB.Bson;

namespace Meshmakers.Octo.SystematizedData.Persistence.SystemStores;

public class OctoUserStore : UserStore<OctoUser, OctoRole, ObjectId>
{
    public OctoUserStore(ISystemContext context)
        : base(((IRepositoryInternal)context.OctoSystemDatabase).GetCollection<OctoUser>().GetMongoCollection(),
            ((IRepositoryInternal)context.OctoSystemDatabase).GetCollection<OctoRole>().GetMongoCollection(),
            new IdentityErrorDescriber())
    {
    }
}
