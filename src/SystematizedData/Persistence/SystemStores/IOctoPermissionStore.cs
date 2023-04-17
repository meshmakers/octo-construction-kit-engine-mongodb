using System.Threading.Tasks;
using Meshmakers.Octo.Backend.Persistence.SystemEntities;

namespace Meshmakers.Octo.Backend.Persistence.SystemStores;

public interface IOctoPermissionStore
{
    Task StorePermissionAsync(OctoPermission octoPermission);
    Task<OctoPermission> GetPermissionById(string permissionId);

    Task EnsurePermission(string permissionId);
}
