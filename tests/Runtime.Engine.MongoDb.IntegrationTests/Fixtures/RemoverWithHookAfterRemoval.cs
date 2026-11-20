using Meshmakers.Octo.Runtime.Engine.MongoDb.Repositories.MongoDb.Generic;
using Meshmakers.Octo.Runtime.Engine.MongoDb.Services.Restore;
using Microsoft.Extensions.Logging.Abstractions;

namespace Meshmakers.Octo.Runtime.Engine.MongoDb.IntegrationTests.Fixtures;

/// <summary>The real removal of platform-only values, followed by a step of the test before the attach.</summary>
internal sealed class RemoverWithHookAfterRemoval(IAdminRepositoryAccess adminRepositoryAccess, Func<Task> afterRemoval)
    : RestoredPlatformOnlyValueRemover(adminRepositoryAccess, NullLogger<RestoredPlatformOnlyValueRemover>.Instance)
{
    public override async Task<IReadOnlyList<string>> RemoveAsync(string databaseName,
        CancellationToken cancellationToken)
    {
        var removedPaths = await base.RemoveAsync(databaseName, cancellationToken);
        await afterRemoval();
        return removedPaths;
    }
}
