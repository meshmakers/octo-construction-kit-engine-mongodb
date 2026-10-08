using FluentAssertions;
using Meshmakers.Octo.ConstructionKit.Contracts;
using Meshmakers.Octo.ConstructionKit.Contracts.BlueprintCatalogs;
using Meshmakers.Octo.ConstructionKit.Contracts.Services;
using Meshmakers.Octo.Runtime.Contracts;
using Meshmakers.Octo.Runtime.Contracts.Blueprints;
using Meshmakers.Octo.Runtime.Contracts.Exchange;
using Meshmakers.Octo.Runtime.Contracts.MongoDb;
using Meshmakers.Octo.Runtime.Contracts.MongoDb.DisplayRules;
using Meshmakers.Octo.Runtime.Contracts.MongoDb.Repositories;
using Meshmakers.Octo.Runtime.Contracts.Repositories.Query;
using Meshmakers.Octo.Runtime.Contracts.RepositoryEntities;
using Meshmakers.Octo.Runtime.Engine.MongoDb.DisplayRules;
using Meshmakers.Octo.Runtime.Engine.MongoDb.IntegrationTests.Collections;
using Meshmakers.Octo.Runtime.Engine.MongoDb.IntegrationTests.Fixtures;
using Meshmakers.Octo.Runtime.Engine.MongoDb.Repositories.MongoDb;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Meshmakers.Octo.Runtime.Engine.MongoDb.IntegrationTests;

/// <summary>
///     AB#5945 end to end against a real MongoDB: blueprint install and UpdateBlueprint (Merge) write
///     their seed through the bulk import, which used to skip the display-rule evaluation - every seeded
///     entity ended up without rtDisplayName and each Merge wiped it again. The abstract
///     <c>Test/Location</c> declares <c>displayNameRule: "${Name}"</c>, inherited by <c>Test/Continent</c>;
///     the TestDnBp blueprint seeds continents. Also covers the operator repair
///     (<see cref="IDisplayRuleRecomputeService" /> + the display-rule sweep) for tenants seeded before
///     the fix.
/// </summary>
[Collection(BlueprintServiceCollection.Name)]
public class DisplayNameBulkPathIntegrationTests(BlueprintServiceFixture fixture)
{
    private static readonly BlueprintId TestDnBpV1 = new("TestDnBp", "1.0.0");
    private static readonly BlueprintId TestDnBpV2 = new("TestDnBp", "2.0.0");
    private static readonly RtCkId<CkTypeId> ContinentCkType = new("Test/Continent");

    [Fact]
    public async Task ApplyBlueprint_SeedEntities_GetRtDisplayName()
    {
        var ct = TestContext.Current.CancellationToken;
        var tenantId = await fixture.CreateTestTenantAsync("dn-apply");
        try
        {
            var result = await fixture.GetBlueprintService().ApplyBlueprintAsync(tenantId, TestDnBpV1, false, ct);
            result.IsSuccess.Should().BeTrue();

            var continents = await QueryContinentsAsync(tenantId);
            continents.Single(c => c.RtWellKnownName == "DnEurope").RtDisplayName.Should().Be("Europe");
            continents.Single(c => c.RtWellKnownName == "DnAsia").RtDisplayName.Should().Be("Asia");
        }
        finally
        {
            await fixture.DropTenantAsync(tenantId);
        }
    }

    [Fact]
    public async Task ApplyUpdate_MergeMode_RecomputesInsteadOfClearing()
    {
        var ct = TestContext.Current.CancellationToken;
        var tenantId = await fixture.CreateTestTenantAsync("dn-merge");
        try
        {
            var blueprintService = fixture.GetBlueprintService();
            (await blueprintService.ApplyBlueprintAsync(tenantId, TestDnBpV1, false, ct)).IsSuccess
                .Should().BeTrue();

            var result = await blueprintService.ApplyUpdateAsync(tenantId, TestDnBpV2, BlueprintUpdateMode.Merge,
                null, ct);
            result.Success.Should().BeTrue();

            var continents = await QueryContinentsAsync(tenantId);
            continents.Single(c => c.RtWellKnownName == "DnEurope").RtDisplayName
                .Should().Be("Europa", "the upserted entity is recomputed");
            continents.Single(c => c.RtWellKnownName == "DnAfrica").RtDisplayName
                .Should().Be("Africa", "the added entity is computed");
            continents.Single(c => c.RtWellKnownName == "DnAsia").RtDisplayName
                .Should().Be("Asia", "Merge leaves entities outside the seed untouched");
        }
        finally
        {
            await fixture.DropTenantAsync(tenantId);
        }
    }

    [Fact]
    public async Task RecomputeService_RepairsEntitiesSeededWithoutDisplayName()
    {
        var ct = TestContext.Current.CancellationToken;
        var tenantId = await fixture.CreateTestTenantAsync("dn-repair");
        try
        {
            (await fixture.GetBlueprintService().ApplyBlueprintAsync(tenantId, TestDnBpV1, false, ct)).IsSuccess
                .Should().BeTrue();
            var tenantContext = await fixture.GetSystemContext().FindTenantContextAsync(tenantId);
            var repository = (TenantRepository)tenantContext.GetTenantRepositoryAsAdmin();

            // Simulate the pre-fix state on prod: seeded entities stored without rtDisplayName
            // (empty string on a partial update = clear sentinel).
            var continents = await QueryContinentsAsync(tenantId);
            var typeGraph = await repository.GetCkTypeGraphAsync(ContinentCkType);
            using (var session = await repository.GetSessionAsync())
            {
                await repository.DataSource.GetRtCollection<RtEntity>(typeGraph).UpdateOneAsync(session,
                    continents.Select(c => new RtEntity(c.GetRtCkTypeId(), c.RtId) { RtDisplayName = string.Empty })
                        .ToList());
            }

            (await QueryContinentsAsync(tenantId)).Should().OnlyContain(c => c.RtDisplayName == null,
                "precondition: display names wiped");

            var recompute = fixture.GetService<IDisplayRuleRecomputeService>();
            var sweepKeys = await recompute.EnqueueRecomputeAsync(tenantId, cancellationToken: ct);
            sweepKeys.Should().Contain(k => k.Contains("/Location"), "Test/Location declares a display rule");

            var updated = await RunSweepsAsync(tenantContext, ct);
            updated.Should().Be(2, "both continents lost their display name");

            var repaired = await QueryContinentsAsync(tenantId);
            repaired.Single(c => c.RtWellKnownName == "DnEurope").RtDisplayName.Should().Be("Europe");
            repaired.Single(c => c.RtWellKnownName == "DnAsia").RtDisplayName.Should().Be("Asia");

            // Idempotent: a second run (single type, inherited rule) enqueues again but writes nothing.
            (await recompute.EnqueueRecomputeAsync(tenantId, "Test/Continent", ct)).Should().ContainSingle();
            (await RunSweepsAsync(tenantContext, ct)).Should().Be(0);
        }
        finally
        {
            await fixture.DropTenantAsync(tenantId);
        }
    }

    [Fact]
    public async Task RecomputeService_TypeWithoutRule_IsRejected()
    {
        var ct = TestContext.Current.CancellationToken;
        var tenantId = await fixture.CreateTestTenantAsync("dn-norule");
        try
        {
            (await fixture.GetBlueprintService().ApplyBlueprintAsync(tenantId, TestDnBpV1, false, ct)).IsSuccess
                .Should().BeTrue();

            var recompute = fixture.GetService<IDisplayRuleRecomputeService>();
            await FluentActions.Awaiting(() => recompute.EnqueueRecomputeAsync(tenantId, "Test/Customer", ct))
                .Should().ThrowAsync<ArgumentException>();
            await FluentActions.Awaiting(() => recompute.EnqueueRecomputeAsync(tenantId, "Test/DoesNotExist", ct))
                .Should().ThrowAsync<ArgumentException>();
        }
        finally
        {
            await fixture.DropTenantAsync(tenantId);
        }
    }

    /// <summary>
    ///     Drains this tenant's sweep tasks synchronously (the background service is not hosted in the
    ///     fixture; the shared store may hold other tests' tasks, so only this tenant's are run).
    /// </summary>
    private async Task<long> RunSweepsAsync(ITenantContext tenantContext, CancellationToken ct)
    {
        var store = fixture.GetService<IDisplayRuleSweepStore>();
        var sweeper = new DisplayRuleSweeper(fixture.GetService<ICkCacheService>(), store,
            NullLogger<DisplayRuleSweeper>.Instance);
        var total = 0L;
        // A sweep over a type above the collection roots fans out into new tasks - loop until drained.
        for (var round = 0; round < 5; round++)
        {
            var records = await store.ListAsync(tenantContext.TenantId, ct);
            if (records.Count == 0)
            {
                break;
            }

            foreach (var record in records)
            {
                total += await sweeper.SweepAsync(tenantContext, record, 500, ct);
                await store.CompleteAsync(record.TenantId, record.CkTypeId, ct);
            }
        }

        return total;
    }

    private async Task<List<RtEntity>> QueryContinentsAsync(string tenantId)
    {
        var repository = await fixture.GetRuntimeRepositoryProvider().GetRepositoryAsync(tenantId);
        repository.Should().NotBeNull();
        using var session = await repository!.GetSessionAsync();
        var resultSet = await repository.GetRtEntitiesByTypeAsync(session, ContinentCkType,
            RtEntityQueryOptions.Create());
        return resultSet.Items.ToList();
    }
}

/// <summary>
///     AB#5945: plain ImportRt (Insert and Upsert) computes rtDisplayName on the bulk path.
/// </summary>
[Collection(ImportTestCkModelCollection.Name)]
public class DisplayNameImportRtIntegrationTests(ImportTestCkModelFixture fixture)
{
    private static readonly OctoObjectId ContinentId = new("66803ecf4aa85720dda96b01");

    private static string Model(string name) => $$"""
        $schema: https://schemas.meshmakers.cloud/runtime-model.schema.json
        dependencies:
          - Test-1.0.0
        entities:
          - rtId: {{ContinentId}}
            ckTypeId: Test/Continent
            attributes:
              - id: Test/Name
                value: {{name}}
        """;

    [Fact]
    public async Task ImportRt_InsertThenUpsert_ComputesAndRecomputesRtDisplayName()
    {
        await fixture.ClearCollectionAsync();
        var import = fixture.GetService<IImportRtModelCommand>();
        var repository = fixture.GetSystemContext().GetTenantRepository();
        var continentType = new RtCkId<CkTypeId>("Test/Continent");
        // Warm the CK cache (ClearCollectionAsync unloads it; the import validates ranges against it).
        await repository.GetCkTypeGraphAsync(continentType);

        await import.ImportTextAsync(repository, Model("Antarctica"), ImportStrategy.Insert);
        (await LoadAsync(repository, continentType)).RtDisplayName.Should().Be("Antarctica");

        await import.ImportTextAsync(repository, Model("Antarktis"), ImportStrategy.Upsert);
        (await LoadAsync(repository, continentType)).RtDisplayName.Should().Be("Antarktis",
            "an upsert replaces the document and must recompute, not clear, the display name");
    }

    private static async Task<RtEntity> LoadAsync(ITenantRepository repository, RtCkId<CkTypeId> continentType)
    {
        using var session = await repository.GetSessionAsync();
        var entities = await repository.GetRtEntitiesByTypeAsync(session, continentType,
            RtEntityQueryOptions.Create());
        return entities.Items.Single(e => e.RtId == ContinentId);
    }
}
