using System.Threading.Tasks;
using Meshmakers.Common.Shared;
using Meshmakers.Octo.Backend.Persistence.DataAccess.Internal;
using Meshmakers.Octo.Backend.Persistence.MongoDb;
using Meshmakers.Octo.Backend.Persistence.SystemEntities;
using Meshmakers.Octo.SystematizedData.Persistence;

namespace Meshmakers.Octo.Backend.Persistence.SystemStores;

public class PermissionStore : IOctoPermissionStore
{
    private readonly ICachedCollection<OctoPermission> _permissionCollection;
    private readonly IRepository _repository;

    public PermissionStore(ISystemContext systemContext)
    {
        _repository = systemContext.SystemDatabase;

        _permissionCollection = _repository.GetCollection<OctoPermission>();
    }

    public async Task StorePermissionAsync(OctoPermission octoPermission)
    {
        var session = await _repository.StartSessionAsync();
        session.StartTransaction();

        var persistentPermission = await GetPermissionById(octoPermission.PermissionId);
        if (persistentPermission == null)
        {
            await _permissionCollection.InsertAsync(session, octoPermission);
        }
        else
        {
            await _permissionCollection.ReplaceByIdAsync(session, persistentPermission.Id,
                octoPermission);
        }

        await session.CommitTransactionAsync();
    }

    public async Task<OctoPermission> GetPermissionById(string permissionId)
    {
        ArgumentValidation.ValidateString(nameof(permissionId), permissionId);

        var session = await _repository.StartSessionAsync();
        session.StartTransaction();

        var result = await _permissionCollection.FindSingleOrDefaultAsync(session, x => x.PermissionId == permissionId);

        await session.CommitTransactionAsync();
        return result;
    }

    public async Task EnsurePermission(string permissionId)
    {
        var permission = await GetPermissionById(permissionId);
        if (permission == null)
        {
            permission = new OctoPermission
            {
                PermissionId = permissionId
            };
            await StorePermissionAsync(permission);
        }
    }
}
