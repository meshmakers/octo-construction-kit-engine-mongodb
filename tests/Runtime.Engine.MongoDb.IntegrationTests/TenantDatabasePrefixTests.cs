using Meshmakers.Octo.Runtime.Contracts.MongoDb;
using Meshmakers.Octo.Runtime.Contracts.MongoDb.Configuration;
using Meshmakers.Octo.Runtime.Engine.MongoDb.IntegrationTests.Collections;
using Meshmakers.Octo.Runtime.Engine.MongoDb.IntegrationTests.Fixtures;

using Microsoft.Extensions.Options;

using MongoDB.Driver;

using Xunit;

namespace Meshmakers.Octo.Runtime.Engine.MongoDb.IntegrationTests;

/// <summary>
///     The tenant-database prefix of an instance (AB#8797): default names carry it, explicit names without it are
///     refused, tenants from before the prefix keep their names, and no prefix behaves as before.
/// </summary>
/// <remarks>
///     The prefix is switched on the bound configuration object (the engine reads it per call), which is how a
///     restart with a new <c>System:TenantDatabasePrefix</c> looks to the create and attach paths. Every test sets it
///     back in <c>finally</c>.
/// </remarks>
[Collection(TenantDatabasePrefixCollection.Name)]
public class TenantDatabasePrefixTests(TenantDatabasePrefixFixture fixture)
{
    private ISystemContext SystemContext => fixture.GetSystemContext();

    private OctoSystemConfiguration Configuration =>
        fixture.GetService<IOptions<OctoSystemConfiguration>>().Value;

    // Unique per run: the MongoDB container is shared with every other collection.
    private static string NewPrefix() => $"p{Guid.NewGuid():N}"[..8] + "_";

    private static string NewId(string stem) => $"{stem}{Guid.NewGuid():N}"[..16].ToLowerInvariant();

    private async Task WithPrefixAsync(string prefix, Func<Task> action)
    {
        var previous = Configuration.TenantDatabasePrefix;
        Configuration.TenantDatabasePrefix = prefix;
        try
        {
            await action();
        }
        finally
        {
            Configuration.TenantDatabasePrefix = previous;
        }
    }

    private async Task CreateTenantAsync(string databaseName, string tenantId)
    {
        using var session = await SystemContext.GetAdminSessionAsync();
        session.StartTransaction();
        await SystemContext.CreateChildTenantAsync(session, databaseName, tenantId);
        await session.CommitTransactionAsync();
    }

    private async Task AttachTenantAsync(string databaseName, string tenantId)
    {
        using var session = await SystemContext.GetAdminSessionAsync();
        session.StartTransaction();
        await SystemContext.AttachChildTenantAsync(session, databaseName, tenantId);
        await session.CommitTransactionAsync();
    }

    private async Task DetachTenantAsync(string tenantId)
    {
        using var session = await SystemContext.GetAdminSessionAsync();
        session.StartTransaction();
        await SystemContext.DetachChildTenantAsync(session, tenantId);
        await session.CommitTransactionAsync();
    }

    private async Task ClearTenantAsync(string tenantId)
    {
        using var session = await SystemContext.GetAdminSessionAsync();
        session.StartTransaction();
        await SystemContext.ClearChildTenantAsync(session, tenantId);
        await session.CommitTransactionAsync();
    }

    private async Task<bool> IsChildTenantExistingAsync(string tenantId)
    {
        using var session = await SystemContext.GetAdminSessionAsync();
        session.StartTransaction();
        var exists = await SystemContext.IsChildTenantExistingAsync(session, tenantId);
        await session.CommitTransactionAsync();
        return exists;
    }

    private async Task DropTenantQuietlyAsync(string tenantId)
    {
        try
        {
            using var session = await SystemContext.GetAdminSessionAsync();
            session.StartTransaction();
            await SystemContext.DropChildTenantAsync(session, tenantId);
            await session.CommitTransactionAsync();
        }
        catch (Exception)
        {
            // Cleanup only - a tenant that was never created must not mask the assertion.
        }
    }

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

    [Fact]
    public async Task WithPrefix_ADefaultNameCarriesThePrefix_AndTheTenantResolves()
    {
        var prefix = NewPrefix();
        var tenantId = NewId("meshtest");

        await WithPrefixAsync(prefix, async () =>
        {
            var databaseName = TenantDatabaseNames.DeriveDefault(Configuration.TenantDatabasePrefix, tenantId);
            Assert.Equal(prefix + tenantId, databaseName);

            try
            {
                await CreateTenantAsync(databaseName, tenantId);

                Assert.True(await SystemContext.IsDatabaseExistingAsync(databaseName));
                Assert.False(await SystemContext.IsDatabaseExistingAsync(tenantId));
                var context = await SystemContext.TryFindTenantContextAsync(tenantId);
                Assert.NotNull(context);
                Assert.Equal(databaseName, context.DatabaseName);
            }
            finally
            {
                await DropTenantQuietlyAsync(tenantId);
            }
        });
    }

    [Fact]
    public async Task WithPrefix_ACreateWithAnExplicitNameWithoutThePrefix_IsRefusedUniformly_AndCreatesNothing()
    {
        var prefix = NewPrefix();
        var tenantId = NewId("explicit");

        await WithPrefixAsync(prefix, async () =>
        {
            try
            {
                var exception = await Assert.ThrowsAsync<TenantException>(() => CreateTenantAsync(tenantId, tenantId));

                // The uniform conflict of AB#4763: no word about the prefix.
                Assert.True(exception.IsConflict);
                Assert.Equal(TenantException.DatabaseNameNotAvailable(tenantId).Message, exception.Message);
                Assert.DoesNotContain(prefix, exception.Message);

                Assert.False(await SystemContext.IsDatabaseExistingAsync(tenantId));
                Assert.False(await IsChildTenantExistingAsync(tenantId));
            }
            finally
            {
                await DropTenantQuietlyAsync(tenantId);
            }
        });
    }

    [Fact]
    public async Task WithPrefix_AnAttachOfADatabaseWithoutThePrefix_IsRefusedUniformly_AndLeavesTheDatabase()
    {
        var prefix = NewPrefix();
        var tenantId = NewId("attach");

        // A database without the prefix, made before the prefix existed and detached again.
        await CreateTenantAsync(tenantId, tenantId);
        await DetachTenantAsync(tenantId);

        try
        {
            await WithPrefixAsync(prefix, async () =>
            {
                var exception = await Assert.ThrowsAsync<TenantException>(() => AttachTenantAsync(tenantId, tenantId));

                Assert.True(exception.IsConflict);
                Assert.Equal(TenantException.DatabaseNameNotAvailable(tenantId).Message, exception.Message);
                Assert.False(await IsChildTenantExistingAsync(tenantId));
                Assert.True(await SystemContext.IsDatabaseExistingAsync(tenantId));
            });

            // Without the prefix the same attach works: only the prefix refused it.
            await AttachTenantAsync(tenantId, tenantId);
            Assert.True(await IsChildTenantExistingAsync(tenantId));
        }
        finally
        {
            await DropTenantQuietlyAsync(tenantId);
        }
    }

    [Fact]
    public async Task WithPrefix_AnAttachOfAPrefixedDatabase_Works()
    {
        var prefix = NewPrefix();
        var tenantId = NewId("attachp");
        var databaseName = prefix + tenantId;

        await WithPrefixAsync(prefix, async () =>
        {
            try
            {
                await CreateTenantAsync(databaseName, tenantId);
                await DetachTenantAsync(tenantId);
                await AttachTenantAsync(databaseName, tenantId);

                Assert.True(await IsChildTenantExistingAsync(tenantId));
            }
            finally
            {
                await DropTenantQuietlyAsync(tenantId);
            }
        });
    }

    [Fact]
    public async Task WithoutPrefix_CreateAndAttachTakeAnyValidName_AsBefore()
    {
        Assert.Equal(string.Empty, Configuration.TenantDatabasePrefix);
        var tenantId = NewId("noprefix");
        var databaseName = NewId("anyname");

        Assert.Equal(tenantId, TenantDatabaseNames.DeriveDefault(Configuration.TenantDatabasePrefix, tenantId));

        try
        {
            await CreateTenantAsync(databaseName, tenantId);
            await DetachTenantAsync(tenantId);
            await AttachTenantAsync(databaseName, tenantId);

            var context = await SystemContext.TryFindTenantContextAsync(tenantId);
            Assert.NotNull(context);
            Assert.Equal(databaseName, context.DatabaseName);
        }
        finally
        {
            await DropTenantQuietlyAsync(tenantId);
        }
    }

    [Fact]
    public async Task ATenantFromBeforeThePrefix_KeepsResolvingAndSurvivesAClear_UnderItsOldName()
    {
        var prefix = NewPrefix();
        var tenantId = NewId("legacy");

        // Registered while the instance had no prefix.
        await CreateTenantAsync(tenantId, tenantId);

        try
        {
            await WithPrefixAsync(prefix, async () =>
            {
                var context = await SystemContext.TryFindTenantContextAsync(tenantId);
                Assert.NotNull(context);
                Assert.Equal(tenantId, context.DatabaseName);

                // Clear drops and re-creates the tenant under the name it had: no rename, no refusal.
                await ClearTenantAsync(tenantId);

                Assert.True(await IsChildTenantExistingAsync(tenantId));
                Assert.True(await SystemContext.IsDatabaseExistingAsync(tenantId));
                Assert.False(await SystemContext.IsDatabaseExistingAsync(prefix + tenantId));

                // The exemption covered only that clear: a new tenant with an unprefixed name is still refused.
                var otherId = NewId("other");
                var exception = await Assert.ThrowsAsync<TenantException>(() => CreateTenantAsync(otherId, otherId));
                Assert.True(exception.IsConflict);
            });
        }
        finally
        {
            await DropTenantQuietlyAsync(tenantId);
        }
    }

    [Fact]
    public async Task WithPrefix_ATakenPrefixedName_IsStillRefused_AndTheDatabaseStaysIntact()
    {
        var prefix = NewPrefix();
        var tenantId = NewId("taken");
        var databaseName = prefix + tenantId;

        // A database of that name that this instance does not know (another instance, an orphan, ...).
        await CreateAdminClient().GetDatabase(databaseName)
            .CreateCollectionAsync("foreign_marker", cancellationToken: TestContext.Current.CancellationToken);

        try
        {
            await WithPrefixAsync(prefix, async () =>
            {
                var exception =
                    await Assert.ThrowsAsync<TenantException>(() => CreateTenantAsync(databaseName, tenantId));

                Assert.True(exception.IsConflict);
                Assert.False(await IsChildTenantExistingAsync(tenantId));
                var collections = await (await CreateAdminClient().GetDatabase(databaseName)
                        .ListCollectionNamesAsync(cancellationToken: TestContext.Current.CancellationToken))
                    .ToListAsync(TestContext.Current.CancellationToken);
                Assert.Contains("foreign_marker", collections);
            });
        }
        finally
        {
            await CreateAdminClient().DropDatabaseAsync(databaseName, TestContext.Current.CancellationToken);
        }
    }

    private static string NewArchivePath() => Path.Combine(Path.GetTempPath(), $"pfxrestore_{Guid.NewGuid():N}.gz");

    private async Task BackupAsync(string tenantId, string archivePath)
    {
        var backup = await SystemContext.BackupTenantAsync(tenantId, archivePath);
        Assert.True(backup.Success, $"Backup should succeed. Error: {backup.Error}");
    }

    private static void DeleteQuietly(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (IOException)
        {
            // Cleanup only.
        }
    }

    [Fact]
    public async Task WithPrefix_ARestoreIntoANewNameWithoutThePrefix_IsRefusedBeforeAnythingIsWritten()
    {
        var prefix = NewPrefix();
        var sourceId = NewId("rsrc");
        var targetId = NewId("rtarget");
        var archivePath = NewArchivePath();

        await CreateTenantAsync(sourceId, sourceId);
        try
        {
            await BackupAsync(sourceId, archivePath);

            await WithPrefixAsync(prefix, async () =>
            {
                var result = await SystemContext.RestoreTenantAsync(targetId, targetId, archivePath);

                Assert.False(result.Success);
                Assert.Contains("tenant-database prefix", result.Error);
                // Refused before mongorestore: no database, no tenant.
                Assert.False(await SystemContext.IsDatabaseExistingAsync(targetId));
                Assert.Null(await SystemContext.TryFindTenantContextAsync(targetId));

                // With the prefix the same restore works.
                var prefixed = await SystemContext.RestoreTenantAsync(targetId, prefix + targetId, archivePath);
                Assert.True(prefixed.Success, $"Restore should succeed. Error: {prefixed.Error}");
                Assert.Equal(prefix + targetId, (await SystemContext.TryFindTenantContextAsync(targetId))!.DatabaseName);
            });
        }
        finally
        {
            await DropTenantQuietlyAsync(targetId);
            await DropTenantQuietlyAsync(sourceId);
            DeleteQuietly(archivePath);
        }
    }

    [Fact]
    public async Task WithPrefix_ARestoreOverATenantFromBeforeThePrefix_KeepsItsName()
    {
        var prefix = NewPrefix();
        var tenantId = NewId("rlegacy");
        var archivePath = NewArchivePath();

        // Registered while the instance had no prefix.
        await CreateTenantAsync(tenantId, tenantId);
        try
        {
            await BackupAsync(tenantId, archivePath);

            await WithPrefixAsync(prefix, async () =>
            {
                var result = await SystemContext.RestoreTenantAsync(tenantId, tenantId, archivePath,
                    dropExistingTenant: true);

                Assert.True(result.Success, $"Restore should succeed. Error: {result.Error}");
                var context = await SystemContext.TryFindTenantContextAsync(tenantId);
                Assert.NotNull(context);
                Assert.Equal(tenantId, context.DatabaseName);

                // The exemption covered only that restore: a new tenant with an unprefixed name is still refused.
                var otherId = NewId("rother");
                var exception = await Assert.ThrowsAsync<TenantException>(() => CreateTenantAsync(otherId, otherId));
                Assert.True(exception.IsConflict);
            });
        }
        finally
        {
            await DropTenantQuietlyAsync(tenantId);
            DeleteQuietly(archivePath);
        }
    }
}
