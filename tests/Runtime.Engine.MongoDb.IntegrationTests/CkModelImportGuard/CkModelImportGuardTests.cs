using System.Text;

using Meshmakers.Octo.ConstructionKit.Contracts;
using Meshmakers.Octo.ConstructionKit.Contracts.DataTransferObjects;
using Meshmakers.Octo.ConstructionKit.Contracts.Serialization;
using Meshmakers.Octo.ConstructionKit.Contracts.Services;
using Meshmakers.Octo.ConstructionKit.Models.System.Generated.System.v2;
using Meshmakers.Octo.Runtime.Contracts.MongoDb;
using Meshmakers.Octo.Runtime.Contracts.MongoDb.Configuration;
using Meshmakers.Octo.Runtime.Engine.MongoDb.IntegrationTests.Fixtures;

using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

using MongoDB.Bson;
using MongoDB.Driver;

using Xunit;

namespace Meshmakers.Octo.Runtime.Engine.MongoDb.IntegrationTests.CkModelImportGuard;

/// <summary>
///     CK v2 F1.0 against a real MongoDB, in throwaway child tenants:
///     <list type="bullet">
///         <item>
///             AB#5900 downgrade guard: a service's embedded model never replaces a newer installed version (same major
///             → INFO, newer major → WARN), an older installed version is upgraded, the same version short-circuits;
///             a skip sends no tenant-update notification; explicit imports may downgrade with a WARN.
///         </item>
///         <item>
///             AB#5901 re-validation: a <c>ResolveFailed</c> model returns to <c>Available</c> after the import that
///             makes its pin satisfiable again (the R2-3 Basic.Accounting scenario), and stays <c>ResolveFailed</c>
///             without flapping while the pin is still unmet.
///         </item>
///     </list>
///     Newer/older System versions are the embedded System compiled model with its id rewritten (same content).
/// </summary>
[Collection(CkModelImportGuardCollection.Name)]
public class CkModelImportGuardTests(CkModelImportGuardFixture fixture)
{
    private static readonly CkModelId TestV1ModelId = new("Test-1.0.0");
    private static CkModelId EmbeddedSystem => SystemCkIds.CkModelId;

    [Fact]
    public async Task EmbeddedSystem_OlderThanInstalledSameMajor_DoesNotReplaceIt_AndSendsNoNotification()
    {
        await WithTenantAsync("guardsame", async (tenant, tenantId) =>
        {
            using var counters = new CkCounterRecorder();
            var newer = Bump(EmbeddedSystem, minor: 1);
            await tenant.ImportCkModelAsync(await RenameAsync(EmbeddedSystem, newer));
            Assert.Equal(newer.FullName, await InstalledAsync(tenantId, "System"));

            var (pre, pos) = (fixture.Notifications.PreUpdates, fixture.Notifications.PosUpdates);
            await ResolveAsync(tenantId); // runs UpdateSystemCkModelAsync with the embedded (older) System

            Assert.Equal(newer.FullName, await InstalledAsync(tenantId, "System"));
            Assert.Equal((pre, pos), (fixture.Notifications.PreUpdates, fixture.Notifications.PosUpdates));
            Assert.NotEmpty(fixture.Logs.Find(LogLevel.Information, "downgrade prevented", tenantId, newer.FullName));
            Assert.True(counters.Sum(CkModelImportDiagnostics.EmbeddedImportSkippedCounterName,
                ("model", "System"), ("reason", CkModelImportDiagnostics.ReasonNewerInstalled)) >= 1);
        });
    }

    [Fact]
    public async Task EmbeddedSystem_InstalledHasHigherMajor_SkipsWithWarning()
    {
        await WithTenantAsync("guardmajor", async (tenant, tenantId) =>
        {
            using var counters = new CkCounterRecorder();
            var nextMajor = new CkModelId("System", new CkVersion(EmbeddedSystem.Version.Major + 1, 0, 0));
            await tenant.ImportCkModelAsync(await RenameAsync(EmbeddedSystem, nextMajor));

            var pos = fixture.Notifications.PosUpdates;
            await ResolveAsync(tenantId);

            Assert.Equal(nextMajor.FullName, await InstalledAsync(tenantId, "System"));
            Assert.Equal(pos, fixture.Notifications.PosUpdates);
            Assert.NotEmpty(fixture.Logs.Find(LogLevel.Warning, "too old for the tenant", tenantId,
                EmbeddedSystem.FullName, nextMajor.FullName));
            Assert.True(counters.Sum(CkModelImportDiagnostics.EmbeddedImportSkippedCounterName,
                ("model", "System"), ("reason", CkModelImportDiagnostics.ReasonNewerMajorInstalled)) >= 1);
        });
    }

    /// <summary>
    ///     Explicit import of an OLDER System is allowed (decision Q5) with a WARN and a counter. It does not stick
    ///     in a process whose embedded System is newer: the next tenant resolve — at the latest, the migration step
    ///     of the explicit import itself resolves the tenant — finds "installed older than embedded" and upgrades it
    ///     back. That is the guard's upgrade rule, not a downgrade.
    /// </summary>
    [Fact]
    public async Task ExplicitSystemDowngrade_IsAllowedWithWarning_AndTheEmbeddedSystemUpgradesItBack()
    {
        await WithTenantAsync("guardolder", async (tenant, tenantId) =>
        {
            using var counters = new CkCounterRecorder();
            Assert.True(EmbeddedSystem.Version.Minor > 0, "test needs a System version below the embedded one");
            var older = new CkModelId("System",
                new CkVersion(EmbeddedSystem.Version.Major, EmbeddedSystem.Version.Minor - 1, 0));

            await tenant.ImportCkModelAsync(await RenameAsync(EmbeddedSystem, older));
            Assert.NotEmpty(fixture.Logs.Find(LogLevel.Warning, "Explicit downgrade", "'System'", tenantId,
                EmbeddedSystem.FullName, older.FullName));
            Assert.True(counters.Sum(CkModelImportDiagnostics.ExplicitDowngradeCounterName, ("model", "System")) >= 1);

            await ResolveAsync(tenantId);
            Assert.Equal(EmbeddedSystem.FullName, await InstalledAsync(tenantId, "System"));
            Assert.NotEmpty(fixture.Logs.Find(LogLevel.Information, "Restoring system CK Model"));
        });
    }

    [Fact]
    public async Task EmbeddedSystem_SameVersion_ShortCircuits()
    {
        await WithTenantAsync("guardequal", async (_, tenantId) =>
        {
            var (pre, pos) = (fixture.Notifications.PreUpdates, fixture.Notifications.PosUpdates);
            await ResolveAsync(tenantId);
            await ResolveAsync(tenantId);

            Assert.Equal(EmbeddedSystem.FullName, await InstalledAsync(tenantId, "System"));
            Assert.Equal((pre, pos), (fixture.Notifications.PreUpdates, fixture.Notifications.PosUpdates));
        });
    }

    /// <summary>
    ///     A service model through the embedded/startup overload <c>ImportCkModelAsync(CkModelId, ...)</c> — the path
    ///     of every service's <c>DefaultConfigurationCreatorService</c> — and through the explicit overload.
    /// </summary>
    [Fact]
    public async Task ServiceModel_EmbeddedOverloadIsGuarded_ExplicitOverloadMayDowngrade()
    {
        await WithTenantAsync("guardsvc", async (tenant, tenantId) =>
        {
            using var counters = new CkCounterRecorder();
            var result = new OperationResult();
            await tenant.ImportCkModelAsync(TestV1ModelId, result);
            Assert.False(result.HasErrors);
            var newer = Bump(TestV1ModelId, minor: 1);
            await tenant.ImportCkModelAsync(await RenameAsync(TestV1ModelId, newer));
            Assert.Equal(newer.FullName, await InstalledAsync(tenantId, "Test"));

            // Embedded: guarded.
            var (pre, pos) = (fixture.Notifications.PreUpdates, fixture.Notifications.PosUpdates);
            await tenant.ImportCkModelAsync(TestV1ModelId, result = new OperationResult());
            await tenant.ImportCkModelWithDowngradeGuardAsync(TestV1ModelId);
            Assert.False(result.HasErrors);
            Assert.Equal(newer.FullName, await InstalledAsync(tenantId, "Test"));
            Assert.Equal((pre, pos), (fixture.Notifications.PreUpdates, fixture.Notifications.PosUpdates));
            Assert.True(counters.Sum(CkModelImportDiagnostics.EmbeddedImportSkippedCounterName,
                ("model", "Test"), ("reason", CkModelImportDiagnostics.ReasonNewerInstalled)) >= 2);

            // Explicit: downgrade allowed, WARN + counter.
            var compiledV1 = await fixture.GetService<ICatalogService>().GetAsync(TestV1ModelId, new OperationResult());
            await tenant.ImportCkModelAsync(compiledV1!);
            Assert.Equal(TestV1ModelId.FullName, await InstalledAsync(tenantId, "Test"));
            Assert.NotEmpty(fixture.Logs.Find(LogLevel.Warning, "Explicit downgrade", "'Test'", newer.FullName));
            Assert.True(counters.Sum(CkModelImportDiagnostics.ExplicitDowngradeCounterName, ("model", "Test")) >= 1);
        });
    }

    /// <summary>
    ///     R2-3 (the Basic.Accounting scenario of Phase 0 E2E run 2) with the v1 contract made visible: an exact-pinned
    ///     model goes <c>ResolveFailed</c> after a System bump, stays there (no downgrade by the embedded System any
    ///     more, no flapping on unrelated imports) and returns to <c>Available</c> by itself after the import that makes
    ///     its pin satisfiable again.
    /// </summary>
    [Fact]
    public async Task ResolveFailedModel_ReturnsToAvailable_WhenItsPinBecomesSatisfiable()
    {
        await WithTenantAsync("revalid", async (tenant, tenantId) =>
        {
            using var counters = new CkCounterRecorder();
            var result = new OperationResult();
            await tenant.ImportCkModelAsync(TestV1ModelId, result);
            Assert.False(result.HasErrors);
            Assert.Equal(1, await StateAsync(tenantId, TestV1ModelId)); // Available

            // Additive System bump: Test-1.0.0 pins the embedded System exactly → ResolveFailed.
            var newer = Bump(EmbeddedSystem, minor: 1);
            await tenant.ImportCkModelAsync(await RenameAsync(EmbeddedSystem, newer));
            Assert.Equal(2, await StateAsync(tenantId, TestV1ModelId)); // ResolveFailed

            // The embedded System no longer reverts the bump, so the model stays ResolveFailed ...
            await ResolveAsync(tenantId);
            Assert.Equal(newer.FullName, await InstalledAsync(tenantId, "System"));
            Assert.Equal(2, await StateAsync(tenantId, TestV1ModelId));

            // ... and an unrelated import re-validates it without flipping it (pin still unmet).
            var stillFailedBefore = counters.Sum(CkModelImportDiagnostics.ModelRevalidatedCounterName,
                ("result", CkModelImportDiagnostics.ResultStillFailed));
            await tenant.ImportCkModelAsync(Unrelated(newer));
            Assert.Equal(2, await StateAsync(tenantId, TestV1ModelId));
            Assert.True(counters.Sum(CkModelImportDiagnostics.ModelRevalidatedCounterName,
                ("result", CkModelImportDiagnostics.ResultStillFailed)) > stillFailedBefore);

            // The import that satisfies the pin again heals it — no manual action.
            await tenant.ImportCkModelAsync(await RenameAsync(EmbeddedSystem, EmbeddedSystem));
            Assert.Equal(1, await StateAsync(tenantId, TestV1ModelId));
            Assert.True(counters.Sum(CkModelImportDiagnostics.ModelRevalidatedCounterName,
                ("result", CkModelImportDiagnostics.ResultRecovered)) >= 1);
            Assert.NotEmpty(fixture.Logs.Find(LogLevel.Information, "resolves again", TestV1ModelId.FullName));

            // Its element rows were flipped back too, so the cache rebuilt from Mongo has its types again.
            var typeStates = await GetTenantDatabase(tenantId).GetCollection<BsonDocument>("CkType")
                .Find(new BsonDocument("ckModelId", TestV1ModelId.FullName))
                .ToListAsync(TestContext.Current.CancellationToken);
            Assert.NotEmpty(typeStates);
            Assert.All(typeStates, t => Assert.Equal(1, t["modelState"].AsInt32));
        });
    }

    private static CkModelId Bump(CkModelId id, int minor) =>
        new(id.Name, new CkVersion(id.Version.Major, id.Version.Minor + minor, 0));

    private static CkCompiledModelRoot Unrelated(CkModelId system) => new()
    {
        ModelId = new CkModelId("GuardUnrelated-1.0.0"),
        Description = "AB#5901 unrelated import that triggers a re-validation",
        Dependencies = [system],
        Attributes = [new CkAttributeDto { AttributeId = new CkAttributeId("Code-1"), ValueType = AttributeValueTypesDto.String }]
    };

    /// <summary>The compiled catalog model of <paramref name="source" /> with every occurrence of its id rewritten.</summary>
    private async Task<CkCompiledModelRoot> RenameAsync(CkModelId source, CkModelId target)
    {
        var compiled = await fixture.GetService<ICatalogService>().GetAsync(source, new OperationResult());
        Assert.NotNull(compiled);
        var serializer = fixture.GetService<ICkJsonSerializer>();
        using var stream = new MemoryStream();
        await using (var writer = new StreamWriter(stream, leaveOpen: true))
        {
            await serializer.SerializeAsync(writer, compiled);
            await writer.FlushAsync(TestContext.Current.CancellationToken);
        }

        var json = Encoding.UTF8.GetString(stream.ToArray()).Replace(source.FullName, target.FullName);
        var operationResult = new OperationResult();
        var renamed = await serializer.DeserializeCompiledModelRootAsync(json, "renamed", operationResult);
        Assert.False(operationResult.HasErrors, string.Join("; ", operationResult.Messages));
        return renamed;
    }

    private async Task ResolveAsync(string tenantId)
    {
        var systemContext = fixture.GetSystemContext();
        using var session = await systemContext.GetAdminSessionAsync();
        session.StartTransaction();
        await systemContext.GetChildTenantContextAsync(session, tenantId);
        await session.CommitTransactionAsync();
    }

    private async Task WithTenantAsync(string prefix, Func<ITenantContext, string, Task> body)
    {
        var systemContext = fixture.GetSystemContext();
        var tenantId = $"{prefix}{Guid.NewGuid():N}"[..20];
        try
        {
            using (var session = await systemContext.GetAdminSessionAsync())
            {
                session.StartTransaction();
                await systemContext.CreateChildTenantAsync(session, tenantId, tenantId);
                await session.CommitTransactionAsync();
            }

            ITenantContext tenant;
            using (var session = await systemContext.GetAdminSessionAsync())
            {
                session.StartTransaction();
                tenant = await systemContext.GetChildTenantContextAsync(session, tenantId);
                await session.CommitTransactionAsync();
            }

            await body(tenant, tenantId);
        }
        finally
        {
            await ThrowawayTenant.DropAsync(systemContext, tenantId);
        }
    }

    private async Task<string> InstalledAsync(string tenantId, string modelName)
    {
        var documents = await GetTenantDatabase(tenantId).GetCollection<BsonDocument>("CkModel")
            .Find(new BsonDocument("modelId", modelName)).ToListAsync(TestContext.Current.CancellationToken);
        return Assert.Single(documents)["_id"].AsString;
    }

    private async Task<int> StateAsync(string tenantId, CkModelId modelId)
    {
        var document = await GetTenantDatabase(tenantId).GetCollection<BsonDocument>("CkModel")
            .Find(new BsonDocument("_id", modelId.FullName)).SingleAsync(TestContext.Current.CancellationToken);
        return document["modelState"].AsInt32;
    }

    private IMongoDatabase GetTenantDatabase(string tenantId)
    {
        var config = fixture.GetService<IOptions<OctoSystemConfiguration>>().Value;
        var urlBuilder = new MongoUrlBuilder
        {
            Server = MongoServerAddress.Parse(config.DatabaseHost),
            Username = config.AdminUser,
            Password = config.AdminUserPassword,
            AuthenticationSource = config.AuthenticationDatabaseName,
            DatabaseName = config.AuthenticationDatabaseName,
            DirectConnection = config.UseDirectConnection
        };
        return new MongoClient(urlBuilder.ToMongoUrl()).GetDatabase(tenantId.ToLowerInvariant());
    }
}
