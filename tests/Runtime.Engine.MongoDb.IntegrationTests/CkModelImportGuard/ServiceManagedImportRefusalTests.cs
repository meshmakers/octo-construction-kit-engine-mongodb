using Meshmakers.Octo.ConstructionKit.Contracts;
using Meshmakers.Octo.ConstructionKit.Contracts.DataTransferObjects;
using Meshmakers.Octo.ConstructionKit.Contracts.Serialization;
using Meshmakers.Octo.ConstructionKit.Models.System.Generated.System.v2;
using Meshmakers.Octo.Runtime.Contracts;
using Meshmakers.Octo.Runtime.Contracts.MongoDb;
using Meshmakers.Octo.Runtime.Contracts.MongoDb.Configuration;
using Meshmakers.Octo.Runtime.Contracts.MongoDb.Repositories;
using Meshmakers.Octo.Runtime.Contracts.Repositories;
using Meshmakers.Octo.Runtime.Contracts.Repositories.Query;
using Meshmakers.Octo.Runtime.Contracts.RepositoryEntities;
using Meshmakers.Octo.Runtime.Engine.MongoDb.IntegrationTests.Fixtures;

using Microsoft.Extensions.Options;

using MongoDB.Bson;
using MongoDB.Driver;

using Xunit;

namespace Meshmakers.Octo.Runtime.Engine.MongoDb.IntegrationTests.CkModelImportGuard;

/// <summary>
///     AB#9325 against a real MongoDB: the caller overload <c>ITenantContext.ImportCkModelAsync(CkCompiledModelRoot)</c> -
///     the engine method behind every caller-initiated import (bot ImportCk / ImportCkBatch jobs, whatever queued them) -
///     refuses every service-managed model (<c>System</c>, <c>System.*</c>) before anything is written. The tenant holds
///     the published System.Identity 3.8.0 (<c>testData/ck-system.identity-3.8.0.json</c>, installed the way the identity
///     service's embedded import does), whose types carry the <c>writeAccess</c> channel of engine message 144.
/// </summary>
[Collection(CkModelImportGuardCollection.Name)]
public class ServiceManagedImportRefusalTests(CkModelImportGuardFixture fixture)
{
    private const string IdentityName = "System.Identity";
    private static readonly CkModelId InstalledIdentity = new(IdentityName, "3.8.0");
    private static readonly RtCkId<CkTypeId> UserTypeId = new("System.Identity/User");

    public static TheoryData<string, string> CallerIdentityVariants => new()
    {
        { "3.9.0", "newer without the writeAccess block" },
        { "3.8.0", "same version with manipulated content" },
        { "3.7.0", "downgrade" },
        { "4.0.0", "next major" }
    };

    [Theory]
    [MemberData(nameof(CallerIdentityVariants))]
    public async Task CallerImportOfSystemIdentity_IsRefused_NothingIsWritten_AndTheInstalledModelStays(string version,
        string variant)
    {
        await WithIdentityTenantAsync("refident", async (tenant, tenantId) =>
        {
            var candidate = await LoadIdentityAsync(version);
            foreach (var type in candidate.Types ?? [])
            {
                type.WriteAccess = null;
            }

            candidate.Description = $"self-built ({variant})";
            var before = await SnapshotAsync(tenantId);
            var (pre, pos) = (fixture.Notifications.PreUpdates, fixture.Notifications.PosUpdates);
            using var counters = new CkCounterRecorder();

            var refusal = await Assert.ThrowsAsync<ServiceManagedCkModelImportRefusedException>(() =>
                tenant.ImportCkModelAsync(candidate));

            Assert.Equal(ServiceManagedCkModels.RefusalCode, refusal.Code);
            Assert.Equal($"{IdentityName}-{version}", refusal.ModelId);
            Assert.StartsWith(ServiceManagedCkModels.RefusalCode, refusal.Message, StringComparison.Ordinal);
            Assert.Contains($"'{IdentityName}-{version}'", refusal.Message, StringComparison.Ordinal);
            Assert.Equal(before, await SnapshotAsync(tenantId));
            Assert.Equal((pre, pos), (fixture.Notifications.PreUpdates, fixture.Notifications.PosUpdates));
            Assert.True(counters.Sum(CkModelImportDiagnostics.ServiceManagedImportRefusedCounterName) >= 1);
            Assert.Equal(0, counters.Sum(CkModelImportDiagnostics.ExplicitDowngradeCounterName, ("model", IdentityName)));

            // The restriction of System.Identity still holds after the refused import: a generic write to an identity
            // type without the identity service's channel is refused with message 144 and leaves nothing behind.
            await AssertGenericUserWriteRefusedAsync(tenant);
        });
    }

    [Theory]
    [InlineData("System")]
    [InlineData("System.StreamData")]
    [InlineData("System.Bot")]
    [InlineData("System.Communication")]
    [InlineData("System.UI")]
    [InlineData("System.Foo")]
    [InlineData("system.identity")]
    [InlineData("System.Identity ")]
    public async Task CallerImportOfAnyServiceManagedName_IsRefused_AlsoWhenTheTenantDoesNotHaveIt(string name)
    {
        await WithTenantAsync("refname", async (tenant, tenantId) =>
        {
            var before = await SnapshotAsync(tenantId);
            var candidate = SmallModel(new CkModelId(name, new CkVersion(9, 0, 0)));

            var refusal = await Assert.ThrowsAsync<ServiceManagedCkModelImportRefusedException>(() =>
                tenant.ImportCkModelAsync(candidate));

            Assert.Equal(ServiceManagedCkModels.RefusalCode, refusal.Code);
            Assert.Equal(before, await SnapshotAsync(tenantId));
        });
    }

    [Theory]
    [InlineData("Customer.System")]
    [InlineData("SystemX")]
    public async Task CallerImportOfANameThatOnlyContainsSystem_IsNotRefused(string name)
    {
        await WithTenantAsync("refnot", async (tenant, tenantId) =>
        {
            var model = SmallModel(new CkModelId(name, new CkVersion(1, 0, 0)));

            await tenant.ImportCkModelAsync(model);

            Assert.Contains(model.ModelId.FullName, await ModelIdsAsync(tenantId));
        });
    }

    /// <summary>
    ///     Abuse case (b): an ordinary model that pins a self-built System.Identity cannot bring it along - a compiled
    ///     model carries only its own elements - and does not resolve: no System.Identity is installed, and the model does
    ///     not become <c>Available</c>.
    /// </summary>
    [Fact]
    public async Task OrdinaryModelDependingOnASelfBuiltSystemIdentity_FailsToResolve_WithoutInstallingIt()
    {
        await WithTenantAsync("refdep", async (tenant, tenantId) =>
        {
            var model = SmallModel(new CkModelId("Customer.Model", new CkVersion(1, 0, 0)));
            model.Dependencies = [SystemCkIds.CkModelId, new CkModelId(IdentityName, "9.9.0")];

            var failure = await Record.ExceptionAsync(() => tenant.ImportCkModelAsync(model));

            Assert.NotNull(failure);
            Assert.IsNotType<ServiceManagedCkModelImportRefusedException>(failure);
            var models = await ModelDocumentsAsync(tenantId);
            Assert.DoesNotContain(models, m => m["modelId"].AsString == IdentityName);
            Assert.DoesNotContain(models,
                m => m["_id"].AsString == model.ModelId.FullName && m["modelState"].AsInt32 == (int)ModelState.Available);
        });
    }

    /// <summary>
    ///     The service path is unchanged: what the identity service's embedded import does (here through the test seam
    ///     that skips only the caller refusal) installs System.Identity, and the embedded System upgrade path keeps working
    ///     next to it (the downgrade-guard tests in <see cref="CkModelImportGuardTests" /> cover the guard itself).
    /// </summary>
    [Fact]
    public async Task ServicePath_InstallsSystemIdentity_AndATenantResolveKeepsIt()
    {
        await WithIdentityTenantAsync("refsvc", async (_, tenantId) =>
        {
            await ResolveAsync(tenantId);

            Assert.Contains(InstalledIdentity.FullName, await ModelIdsAsync(tenantId));
            Assert.Contains(SystemCkIds.CkModelId.FullName, await ModelIdsAsync(tenantId));
        });
    }

    private static CkCompiledModelRoot SmallModel(CkModelId modelId) => new()
    {
        ModelId = modelId,
        Description = "AB#9325 refusal test model",
        Dependencies = [SystemCkIds.CkModelId],
        Attributes =
            [new CkAttributeDto { AttributeId = new CkAttributeId("RefusalCode-1"), ValueType = AttributeValueTypesDto.String }]
    };

    private async Task<CkCompiledModelRoot> LoadIdentityAsync(string version)
    {
        var path = Path.Combine(AppContext.BaseDirectory, "testData", "ck-system.identity-3.8.0.json");
        var json = (await File.ReadAllTextAsync(path, TestContext.Current.CancellationToken))
            .Replace(InstalledIdentity.FullName, $"{IdentityName}-{version}", StringComparison.Ordinal);
        var result = new OperationResult();
        var model = await fixture.GetService<ICkJsonSerializer>().DeserializeCompiledModelRootAsync(json, path, result);
        Assert.False(result.HasErrors, string.Join("; ", result.Messages));
        return model;
    }

    private static async Task AssertGenericUserWriteRefusedAsync(ITenantContext tenant)
    {
        var repository = tenant.GetTenantRepository();
        var rtId = OctoObjectId.GenerateNewId();
        var caller = RtSecurityContext.ForUser("caller-1", ["TenantManagement"], RtPrincipalKind.Interactive);
        var refused = await Assert.ThrowsAsync<RuntimeRepositoryException>(async () =>
        {
            using var session = await repository.GetSessionAsync(caller);
            session.StartTransaction();
            var entity = new RtEntity(UserTypeId, rtId, new Dictionary<string, object?>());
            await repository.ApplyChangesAsync(session, [EntityUpdateInfo<RtEntity>.CreateInsert(UserTypeId, entity)],
                new OperationResult());
            await session.CommitTransactionAsync();
        });
        Assert.Contains(refused.OperationResult.Messages, m => m.MessageNumber == 144);

        using var readSession = await repository.GetSessionAsync();
        var found = await repository.GetRtEntitiesByIdAsync(readSession, UserTypeId, [rtId], RtEntityQueryOptions.Create());
        Assert.Empty(found.Items);
    }

    private async Task WithIdentityTenantAsync(string prefix, Func<ITenantContext, string, Task> body)
    {
        await WithTenantAsync(prefix, async (tenant, tenantId) =>
        {
            await CkModelImportGuardTests.InstallAsServiceAsync(tenant, await LoadIdentityAsync("3.8.0"));
            Assert.Contains(InstalledIdentity.FullName, await ModelIdsAsync(tenantId));
            await body(tenant, tenantId);
        });
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

    private async Task ResolveAsync(string tenantId)
    {
        var systemContext = fixture.GetSystemContext();
        using var session = await systemContext.GetAdminSessionAsync();
        session.StartTransaction();
        await systemContext.GetChildTenantContextAsync(session, tenantId);
        await session.CommitTransactionAsync();
    }

    /// <summary>Every CK document of the tenant (all <c>Ck*</c> collections), as a comparable text.</summary>
    private async Task<string> SnapshotAsync(string tenantId)
    {
        var database = GetTenantDatabase(tenantId);
        var names = (await (await database.ListCollectionNamesAsync(cancellationToken: TestContext.Current.CancellationToken))
                .ToListAsync(TestContext.Current.CancellationToken))
            .Where(n => n.StartsWith("Ck", StringComparison.Ordinal))
            .Order(StringComparer.Ordinal);
        var parts = new List<string>();
        foreach (var name in names)
        {
            var documents = await database.GetCollection<BsonDocument>(name).Find(FilterDefinition<BsonDocument>.Empty)
                .Sort(Builders<BsonDocument>.Sort.Ascending("_id")).ToListAsync(TestContext.Current.CancellationToken);
            parts.Add($"{name}:{string.Join("|", documents.Select(d => d.ToJson()))}");
        }

        return string.Join("\n", parts);
    }

    private async Task<List<BsonDocument>> ModelDocumentsAsync(string tenantId) =>
        await GetTenantDatabase(tenantId).GetCollection<BsonDocument>("CkModel").Find(FilterDefinition<BsonDocument>.Empty)
            .ToListAsync(TestContext.Current.CancellationToken);

    private async Task<List<string>> ModelIdsAsync(string tenantId) =>
        (await ModelDocumentsAsync(tenantId)).Select(d => d["_id"].AsString).ToList();

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
