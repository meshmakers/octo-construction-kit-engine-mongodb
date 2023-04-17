using System.Threading.Tasks;
using Meshmakers.Octo.SystematizedData.Persistence.SystemEntities;

namespace Meshmakers.Octo.SystematizedData.Persistence.SystemStores;

public interface IOctoPermissionStore
{
    Task StorePermissionAsync(OctoPermission octoPermission);
    Task<OctoPermission> GetPermissionById(string permissionId);

    Task EnsurePermission(string permissionId);
}
