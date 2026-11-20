using Meshmakers.Octo.Runtime.Contracts.MongoDb.Configuration;
using Meshmakers.Octo.Runtime.Contracts.MongoDb.Services;
using Meshmakers.Octo.Runtime.Contracts.MongoDb.TenantLifecycle;
using Meshmakers.Octo.Runtime.Engine.MongoDb.Repositories.MongoDb.Generic;
using Meshmakers.Octo.Runtime.Engine.MongoDb.Repositories.TenantOwnership;
using Meshmakers.Octo.Runtime.Engine.MongoDb.Services;
using Meshmakers.Octo.Runtime.Engine.MongoDb.Services.Restore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Meshmakers.Octo.Runtime.Engine.MongoDb.IntegrationTests.Fixtures;

/// <summary>Builds the backup service of the system fixture with a replaced mongorestore and, optionally, remover.</summary>
internal static class RestoreBackupServiceFactory
{
    public static TenantBackupService Create(SystemFixture systemFixture,
        Func<IRepositoryOpsService, MongoRestoreOptions, Task<CommandResult>> mongoRestoreReplacement,
        RestoredPlatformOnlyValueRemover? platformOnlyValueRemover = null)
    {
        var provider = systemFixture.Provider!;
        var repositoryOps = new InterceptedMongoRestore(provider.GetRequiredService<IRepositoryOpsService>(),
            mongoRestoreReplacement);
        var remover = platformOnlyValueRemover ?? CreateRemover(systemFixture);
        return new TenantBackupService(systemFixture.GetSystemContext(), repositoryOps,
            NullLogger<TenantBackupService>.Instance,
            provider.GetRequiredService<TenantOwnershipStore>(),
            provider.GetRequiredService<TenantDatabaseRegistrationLookup>(),
            provider.GetRequiredService<SystemDatabaseMarkerStore>(),
            remover,
            provider.GetService<ITenantLifecycleStore>(), provider.GetService<IOptions<OctoSystemConfiguration>>());
    }

    public static RestoredPlatformOnlyValueRemover CreateRemover(SystemFixture systemFixture) =>
        new(systemFixture.Provider!.GetRequiredService<IAdminRepositoryAccess>(),
            NullLogger<RestoredPlatformOnlyValueRemover>.Instance);
}
