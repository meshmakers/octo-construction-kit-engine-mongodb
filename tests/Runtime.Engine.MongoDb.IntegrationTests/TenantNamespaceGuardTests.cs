using Meshmakers.Common.Shared;
using Meshmakers.Octo.ConstructionKit.Models.System.Generated.System.v2;
using Meshmakers.Octo.Runtime.Contracts.MongoDb;
using Meshmakers.Octo.Runtime.Contracts.MongoDb.Configuration;
using Meshmakers.Octo.Runtime.Engine.MongoDb.IntegrationTests.Collections;
using Meshmakers.Octo.Runtime.Engine.MongoDb.IntegrationTests.Fixtures;

using Microsoft.Extensions.Options;

using MongoDB.Bson;
using MongoDB.Driver;

using Xunit;

namespace Meshmakers.Octo.Runtime.Engine.MongoDb.IntegrationTests;

/// <summary>
///     Guards the tenant-id and database-name namespaces (AB#4763) and, above all, that a rejected
///     create never destroys a database it did not create (AB#4762).
/// </summary>
[Collection(TenantNamespaceCollection.Name)]
public class TenantNamespaceGuardTests(TenantNamespaceFixture fixture)
{
    private ISystemContext SystemContext => fixture.GetSystemContext();

    private OctoSystemConfiguration Configuration =>
        fixture.GetService<IOptions<OctoSystemConfiguration>>().Value;

    private static string NewName(string prefix) => $"{prefix}{Guid.NewGuid():N}"[..20].ToLowerInvariant();

    private async Task CreateTenantAsync(ITenantContext parent, string databaseName, string tenantId)
    {
        using var session = await parent.GetAdminSessionAsync();
        session.StartTransaction();
        await parent.CreateChildTenantAsync(session, databaseName, tenantId);
        await session.CommitTransactionAsync();
    }

    private async Task DropTenantQuietlyAsync(ITenantContext parent, string tenantId)
    {
        try
        {
            using var session = await parent.GetAdminSessionAsync();
            session.StartTransaction();
            await parent.DropChildTenantAsync(session, tenantId);
            await session.CommitTransactionAsync();
        }
        catch (Exception)
        {
            // Cleanup only — a tenant that was never created must not mask the assertion failure.
        }
    }

    private async Task<bool> IsChildTenantExistingAsync(ITenantContext parent, string tenantId)
    {
        using var session = await parent.GetAdminSessionAsync();
        session.StartTransaction();
        var exists = await parent.IsChildTenantExistingAsync(session, tenantId);
        await session.CommitTransactionAsync();
        return exists;
    }

    /// <summary>
    ///     Opens an admin-credentialed driver connection. Needed because the engine exposes no API for
    ///     "does this database user exist", and the AB#4762 rollback dropped the user as well as the
    ///     database. Mirrors the helper in <c>CkModelImportChangeStreamTests</c>.
    /// </summary>
    private MongoClient CreateAdminClient()
    {
        var config = Configuration;
        var urlBuilder = new MongoUrlBuilder
        {
            Server = MongoServerAddress.Parse(config.DatabaseHost),
            Username = config.AdminUser,
            Password = config.AdminUserPassword,
            AuthenticationSource = config.AuthenticationDatabaseName,
            DatabaseName = config.AuthenticationDatabaseName,
            DirectConnection = config.UseDirectConnection
        };

        return new MongoClient(urlBuilder.ToMongoUrl());
    }

    private async Task<bool> IsDatabaseUserExistingAsync(string normalizedDatabaseName)
    {
        var config = Configuration;
        var userName = string.Format(config.DatabaseUser, normalizedDatabaseName);
        var authDatabase = CreateAdminClient().GetDatabase(config.AuthenticationDatabaseName);

        var result = await authDatabase.RunCommandAsync<BsonDocument>(
            new BsonDocumentCommand<BsonDocument>(new BsonDocument("usersInfo", userName)));

        return result.GetValue("ok", 0).ToDouble() > 0
               && result.GetValue("users", new BsonArray()).AsBsonArray.Count > 0;
    }

    [Fact]
    public async Task CreateChildTenant_WithDatabaseOfAnotherTenant_ConflictsAndLeavesItIntact()
    {
        var victimId = NewName("victim");
        var intruderId = NewName("intruder");

        await CreateTenantAsync(SystemContext, victimId, victimId);

        try
        {
            // Sanity: the victim really is there before the rejected create.
            Assert.True(await SystemContext.IsDatabaseExistingAsync(victimId));
            Assert.True(await IsDatabaseUserExistingAsync(victimId));

            var exception = await Assert.ThrowsAsync<TenantException>(async () =>
                await CreateTenantAsync(SystemContext, victimId, intruderId));

            Assert.True(exception.IsConflict);
            Assert.Contains("is not available", exception.Message);
            // Nothing about the incumbent may leak into the message.
            Assert.DoesNotContain(victimId, exception.Message.Replace($"'{victimId}'", string.Empty));

            // The whole point of AB#4762: the rejected create must not have touched the victim.
            Assert.True(await SystemContext.IsDatabaseExistingAsync(victimId));
            Assert.True(await IsDatabaseUserExistingAsync(victimId));
            Assert.True(await IsChildTenantExistingAsync(SystemContext, victimId));
        }
        finally
        {
            await DropTenantQuietlyAsync(SystemContext, victimId);
            await DropTenantQuietlyAsync(SystemContext, intruderId);
        }
    }

    [Fact]
    public async Task CreateChildTenant_WithTenantIdUsedInAnotherSubtree_Conflicts()
    {
        var branchAId = NewName("brancha");
        var branchBId = NewName("branchb");
        var sharedId = NewName("shared");
        var otherDatabase = NewName("otherdb");

        await CreateTenantAsync(SystemContext, branchAId, branchAId);
        await CreateTenantAsync(SystemContext, branchBId, branchBId);

        var branchA = await SystemContext.GetChildTenantContextAsync(branchAId);
        var branchB = await SystemContext.GetChildTenantContextAsync(branchBId);

        try
        {
            await CreateTenantAsync(branchA, sharedId, sharedId);

            // Branch B cannot see branch A's children, which is exactly why the check has to consult
            // the platform-wide registry (AB#4763).
            var exception = await Assert.ThrowsAsync<TenantException>(async () =>
                await CreateTenantAsync(branchB, otherDatabase, sharedId));

            Assert.True(exception.IsConflict);
            Assert.Contains("already in use", exception.Message);

            // The incumbent still resolves to its own database, and the rejected create left nothing.
            Assert.True(await IsChildTenantExistingAsync(branchA, sharedId));
            Assert.False(await IsChildTenantExistingAsync(branchB, sharedId));
            Assert.False(await SystemContext.IsDatabaseExistingAsync(otherDatabase));
        }
        finally
        {
            await DropTenantQuietlyAsync(branchA, sharedId);
            await DropTenantQuietlyAsync(SystemContext, branchAId);
            await DropTenantQuietlyAsync(SystemContext, branchBId);
        }
    }

    [Fact]
    public async Task CreateChildTenant_WithSystemTenantId_ConflictsAndLeavesSystemTenantIntact()
    {
        // Read both from configuration: the test fixture overrides SystemDatabaseName but NOT
        // SystemTenantId, so hard-coding either would silently assert the wrong thing.
        var systemTenantId = Configuration.SystemTenantId;
        var databaseName = NewName("sysid");

        var exception = await Assert.ThrowsAsync<TenantException>(async () =>
            await CreateTenantAsync(SystemContext, databaseName, systemTenantId));

        Assert.True(exception.IsConflict);
        Assert.Contains("already in use", exception.Message);

        Assert.True(await SystemContext.IsSystemTenantExistingAsync());
        Assert.False(await SystemContext.IsDatabaseExistingAsync(databaseName));
    }

    [Fact]
    public async Task CreateChildTenant_WithSystemDatabaseName_ConflictsAndLeavesSystemDatabaseIntact()
    {
        var systemDatabaseName = Configuration.SystemDatabaseName;
        var tenantId = NewName("sysdb");

        var exception = await Assert.ThrowsAsync<TenantException>(async () =>
            await CreateTenantAsync(SystemContext, systemDatabaseName, tenantId));

        Assert.True(exception.IsConflict);
        Assert.Contains("is not available", exception.Message);

        // If this regresses, the whole platform database is gone.
        Assert.True(await SystemContext.IsSystemTenantExistingAsync());
        Assert.True(await SystemContext.IsDatabaseExistingAsync(systemDatabaseName));
    }

    [Fact]
    public async Task AttachChildTenant_WithDatabaseClaimedByAnotherTenant_Conflicts()
    {
        var ownerId = NewName("owner");
        var thiefId = NewName("thief");

        await CreateTenantAsync(SystemContext, ownerId, ownerId);

        try
        {
            var exception = await Assert.ThrowsAsync<TenantException>(async () =>
            {
                using var session = await SystemContext.GetAdminSessionAsync();
                session.StartTransaction();
                await SystemContext.AttachChildTenantAsync(session, ownerId, thiefId);
                await session.CommitTransactionAsync();
            });

            Assert.True(exception.IsConflict);
            Assert.Contains("is not available", exception.Message);

            Assert.True(await SystemContext.IsDatabaseExistingAsync(ownerId));
            Assert.False(await IsChildTenantExistingAsync(SystemContext, thiefId));
        }
        finally
        {
            await DropTenantQuietlyAsync(SystemContext, thiefId);
            await DropTenantQuietlyAsync(SystemContext, ownerId);
        }
    }

    [Fact]
    public async Task DetachAndReattachChildTenant_FromSubTenant_Succeeds()
    {
        var parentId = NewName("parent");
        // Mixed case on purpose: attach used to store the raw values while every lookup normalizes.
        var childId = $"Child{Guid.NewGuid():N}"[..16];
        var childDatabase = childId.ToUpperInvariant();

        await CreateTenantAsync(SystemContext, parentId, parentId);
        var parent = await SystemContext.GetChildTenantContextAsync(parentId);

        try
        {
            await CreateTenantAsync(parent, childDatabase, childId);

            using (var session = await parent.GetAdminSessionAsync())
            {
                session.StartTransaction();
                await parent.DetachChildTenantAsync(session, childId);
                await session.CommitTransactionAsync();
            }

            // Detach must clear the platform-wide record too, otherwise the now-global uniqueness
            // check would reject every re-attach (AB#4763).
            using (var session = await parent.GetAdminSessionAsync())
            {
                session.StartTransaction();
                await parent.AttachChildTenantAsync(session, childDatabase, childId);
                await session.CommitTransactionAsync();
            }

            Assert.True(await IsChildTenantExistingAsync(parent, childId));

            using var readSession = await parent.GetAdminSessionAsync();
            readSession.StartTransaction();
            var reattached = await parent.GetChildTenantAsync(readSession, childId);
            await readSession.CommitTransactionAsync();

            Assert.Equal(childId.NormalizeString(), reattached.TenantId);
            Assert.Equal(childDatabase.ToLowerInvariant(), reattached.DatabaseName);
        }
        finally
        {
            await DropTenantQuietlyAsync(parent, childId);
            await DropTenantQuietlyAsync(SystemContext, parentId);
        }
    }

    [Fact]
    public async Task AttachChildTenant_WithMissingDatabase_ConflictsIndistinguishablyFromAClaimedOne()
    {
        var ownerId = NewName("oracle");
        var absentDatabase = NewName("absentdb");

        await CreateTenantAsync(SystemContext, ownerId, ownerId);

        try
        {
            async Task<TenantException> AttachAsync(string database, string tenantId)
                => await Assert.ThrowsAsync<TenantException>(async () =>
                {
                    using var session = await SystemContext.GetAdminSessionAsync();
                    session.StartTransaction();
                    await SystemContext.AttachChildTenantAsync(session, database, tenantId);
                    await session.CommitTransactionAsync();
                });

            var claimed = await AttachAsync(ownerId, NewName("thief"));
            var absent = await AttachAsync(absentDatabase, NewName("prober"));

            // Both must be the same conflict. Answering "does not exist" for the free name turned attach
            // into a cluster-wide database-existence oracle (AB#4763).
            Assert.True(claimed.IsConflict);
            Assert.True(absent.IsConflict);
            Assert.Equal(
                claimed.Message.Replace(ownerId, "X"),
                absent.Message.Replace(absentDatabase, "X"));
        }
        finally
        {
            await DropTenantQuietlyAsync(SystemContext, ownerId);
        }
    }

    [Theory]
    [InlineData("admin")]
    [InlineData("local")]
    [InlineData("config")]
    public async Task AttachChildTenant_WithAMongoDbOwnedDatabase_Conflicts(string reservedDatabase)
    {
        // These exist, so attach would otherwise adopt them as ordinary tenant databases — and the next
        // delete would drop MongoDB's own state.
        var exception = await Assert.ThrowsAsync<TenantException>(async () =>
        {
            using var session = await SystemContext.GetAdminSessionAsync();
            session.StartTransaction();
            await SystemContext.AttachChildTenantAsync(session, reservedDatabase, NewName("adopt"));
            await session.CommitTransactionAsync();
        });

        Assert.True(exception.IsConflict);
        Assert.Contains("is not available", exception.Message);
        Assert.True(await SystemContext.IsDatabaseExistingAsync(reservedDatabase));
    }

    [Fact]
    public async Task CreateChildTenant_WithALongButLegalDatabaseName_Works()
    {
        // The driver caps the connection ApplicationName at 128 bytes and the database name appears in
        // it twice (once directly, once inside the per-tenant user), so names beyond roughly 30
        // characters used to produce a tenant that provisioned halfway and then threw on every
        // background tick forever. The name is now clamped where it is built, so any name MongoDB
        // itself accepts must work.
        var longDatabase = "ab4762long" + new string('y', 40);
        var tenantId = NewName("longdb");
        Assert.True(longDatabase.Length > 30 && longDatabase.Length <= 63);

        await CreateTenantAsync(SystemContext, longDatabase, tenantId);

        try
        {
            Assert.True(await SystemContext.IsDatabaseExistingAsync(longDatabase));

            using var session = await SystemContext.GetAdminSessionAsync();
            session.StartTransaction();
            var resolved = await SystemContext.GetChildTenantAsync(session, tenantId);
            await session.CommitTransactionAsync();

            Assert.Equal(longDatabase, resolved.DatabaseName);
        }
        finally
        {
            await DropTenantQuietlyAsync(SystemContext, tenantId);
        }
    }

    [Fact]
    public async Task CreateChildTenant_WithAnIllegalDatabaseName_IsRejectedBeforeAnySideEffect()
    {
        var illegal = "ab4762$bad";

        await Assert.ThrowsAsync<ArgumentException>(async () =>
            await CreateTenantAsync(SystemContext, illegal, NewName("illegal")));

        Assert.False(await SystemContext.IsDatabaseExistingAsync(illegal));
    }

    [Fact]
    public async Task DropChildTenant_WithLegacyRawCasedRegistryRecord_RemovesRecordAndDatabase()
    {
        // The pre-AB#4763 attach wrote the operator's RAW casing into the subtree-local registry
        // (only the system-registry record was normalized) and adopted the physical database under
        // whatever casing it had. The registry-delete filter must therefore match the stored value
        // verbatim, and the physical drop must cover both spellings — a normalized-only filter/drop
        // makes such a tenant survive its own deletion (record and database both stay behind).
        var parentId = NewName("legparent");
        var childId = NewName("legchild");
        var mixedCaseDatabase = $"Ab4762Legacy{Guid.NewGuid():N}"[..20];
        Assert.NotEqual(mixedCaseDatabase, mixedCaseDatabase.ToLowerInvariant());

        await CreateTenantAsync(SystemContext, parentId, parentId);
        var parent = await SystemContext.GetChildTenantContextAsync(parentId);

        try
        {
            // Adopted physical database, with its original mixed casing.
            await CreateAdminClient().GetDatabase(mixedCaseDatabase)
                .CreateCollectionAsync("legacy_marker", cancellationToken: TestContext.Current.CancellationToken);

            // Registry records exactly as the legacy attach wrote them: raw into the parent's local
            // registry, normalized into the platform-wide system registry.
            using (var session = await parent.GetAdminSessionAsync())
            {
                session.StartTransaction();
                await parent.GetTenantRepositoryAsAdmin().InsertOneRtEntityAsync(session,
                    new RtTenant { TenantId = childId, DatabaseName = mixedCaseDatabase });
                await parent.GetSystemTenantRepositoryAsAdmin().InsertOneRtEntityAsync(session,
                    new RtTenant
                    {
                        TenantId = childId, ParentTenantId = parentId,
                        DatabaseName = mixedCaseDatabase.ToLowerInvariant()
                    });
                await session.CommitTransactionAsync();
            }

            Assert.True(await IsChildTenantExistingAsync(parent, childId));
            Assert.True(await SystemContext.IsDatabaseExistingAsync(mixedCaseDatabase));

            using (var session = await parent.GetAdminSessionAsync())
            {
                session.StartTransaction();
                await parent.DropChildTenantAsync(session, childId);
                await session.CommitTransactionAsync();
            }

            // Local record gone (verbatim filter) and physical database gone (both-spellings drop).
            Assert.False(await IsChildTenantExistingAsync(parent, childId));
            Assert.False(await SystemContext.IsDatabaseExistingAsync(mixedCaseDatabase));

            // The system-registry record is gone too: the now-global tenant-id check would otherwise
            // reject this re-create.
            var recreateDatabase = NewName("legredo");
            await CreateTenantAsync(parent, recreateDatabase, childId);
            Assert.True(await IsChildTenantExistingAsync(parent, childId));
        }
        finally
        {
            await DropTenantQuietlyAsync(parent, childId);
            await DropTenantQuietlyAsync(SystemContext, parentId);
        }
    }

    [Fact]
    public async Task ClearChildTenant_RecreatesTheSameTenant()
    {
        var tenantId = NewName("clear");

        await CreateTenantAsync(SystemContext, tenantId, tenantId);

        try
        {
            // Clear drops and immediately re-creates the same id and database name. It only survives
            // the new guards because they run on the caller's session and therefore see the
            // uncommitted deletes — worth pinning against a later refactor to a fresh session.
            using (var session = await SystemContext.GetAdminSessionAsync())
            {
                session.StartTransaction();
                await SystemContext.ClearChildTenantAsync(session, tenantId);
                await session.CommitTransactionAsync();
            }

            Assert.True(await IsChildTenantExistingAsync(SystemContext, tenantId));
            Assert.True(await SystemContext.IsDatabaseExistingAsync(tenantId));
        }
        finally
        {
            await DropTenantQuietlyAsync(SystemContext, tenantId);
        }
    }
}
