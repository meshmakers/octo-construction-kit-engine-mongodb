using Persistence.IdentityCkModel.ConstructionKit.Generated.System.Identity.v1;

namespace Meshmakers.Octo.Backend.Persistence.SystemStores;

public interface IOctoPermissionStore
{
    Task StorePermissionAsync(RtPermission octoPermission);
    Task<RtPermission?> GetPermissionById(string permissionId);

    Task EnsurePermission(string permissionId);
}
