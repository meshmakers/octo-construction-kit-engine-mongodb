using Meshmakers.Octo.ConstructionKit.Contracts;
using Meshmakers.Octo.ConstructionKit.Contracts.DataTransferObjects;
using Meshmakers.Octo.ConstructionKit.Models.System.Generated.System.v2;
using Meshmakers.Octo.Runtime.Contracts;
using Meshmakers.Octo.Runtime.Contracts.Exchange;
using Meshmakers.Octo.Runtime.Contracts.MongoDb;
using Meshmakers.Octo.Runtime.Contracts.MongoDb.Repositories;
using Meshmakers.Octo.Runtime.Contracts.Repositories;
using Meshmakers.Octo.Runtime.Contracts.Repositories.Query;
using Meshmakers.Octo.Runtime.Contracts.RepositoryEntities;
using Meshmakers.Octo.Runtime.Engine.MongoDb.IntegrationTests.Collections;
using Meshmakers.Octo.Runtime.Engine.MongoDb.IntegrationTests.Fixtures;

using TestCkModel.Generated.Test.v1;

using Xunit;

namespace Meshmakers.Octo.Runtime.Engine.MongoDb.IntegrationTests;

/// <summary>
///     AB#7018: the default delete (GraphQL <c>deleteEntities</c>) only archives a runtime entity: the document
///     stays in the collection with its <c>_id</c> (= rtId). An import of the same entity must not fail with the
///     raw E11000 of the primary key. Decision (Odin): <c>InsertOnly</c> treats an ARCHIVED document with the
///     same rtId as free and replaces it; an ACTIVE one still fails, with a message that names the state.
///     A plain <c>Unique</c> CK index keeps blocking the value of an archived entity (unchanged semantics), but
///     now says so; <c>UniqueNotDeleted</c> frees it.
/// </summary>
[Collection(ImportTestCkModelCollection.Name)]
public class ImportAfterArchiveTests(ImportTestCkModelFixture fixture)
{
    private static readonly OctoObjectId Europe = new("66803ecf4aa85720dda96a97");
    private static readonly OctoObjectId Austria = new("66803ecf4aa85720dda96a98");

    private static string ContinentModel(OctoObjectId rtId, string name) => $$"""
        $schema: https://schemas.meshmakers.cloud/runtime-model.schema.json
        dependencies:
          - Test-1.0.0
        entities:
          - rtId: {{rtId}}
            ckTypeId: Test/Continent
            attributes:
              - id: Test/Name
                value: {{name}}
        """;

    private static string CountryUnderContinentModel() => $$"""
        $schema: https://schemas.meshmakers.cloud/runtime-model.schema.json
        dependencies:
          - Test-1.0.0
        entities:
          - rtId: {{Europe}}
            ckTypeId: Test/Continent
            attributes:
              - id: Test/Name
                value: Europe
          - rtId: {{Austria}}
            ckTypeId: Test/Country
            attributes:
              - id: Test/Name
                value: Oesterreich
              - id: Test/ISOCode
                value: AT
            associations:
              - roleId: System/ParentChild
                targetRtId: {{Europe}}
                targetCkTypeId: Test/Continent
        """;

    private async Task<ITenantRepository> StartAsync()
    {
        await fixture.ClearCollectionAsync();
        var repository = fixture.GetSystemContext().GetTenantRepository();
        // The import command validates against the CK cache directly; warm it like any real caller.
        await repository.GetCkTypeGraphAsync(TestCkIds.RtCkCountryTypeId);
        return repository;
    }

    private Task ImportAsync(IRuntimeRepository repository, string model, ImportStrategy strategy) =>
        fixture.GetService<IImportRtModelCommand>().ImportTextAsync(repository, model, strategy);

    private static async Task DeleteAsync(ITenantRepository repository, RtCkId<CkTypeId> ckTypeId, OctoObjectId rtId,
        DeleteOptions? options = null)
    {
        using var session = await repository.GetSessionAsync();
        session.StartTransaction();
        var result = new OperationResult();
        var updates = new[] { EntityUpdateInfo<RtEntity>.CreateDelete(new RtEntityId(ckTypeId, rtId)) };
        if (options == null)
        {
            // default options: Archive, exactly what the GraphQL delete does
            await repository.ApplyChangesAsync(session, updates, result);
        }
        else
        {
            await repository.ApplyChangesAsync(session, updates, options, result);
        }

        Assert.False(result.HasErrors);
        await session.CommitTransactionAsync();
    }

    private static async Task<RtEntity?> LoadAsync(ITenantRepository repository, RtCkId<CkTypeId> ckTypeId,
        OctoObjectId rtId)
    {
        using var session = await repository.GetSessionAsync();
        return await repository.GetRtEntityByRtIdAsync(session, new RtEntityId(ckTypeId, rtId));
    }

    private static async Task<RtContinent?> LoadContinentAsync(ITenantRepository repository, OctoObjectId rtId)
    {
        using var session = await repository.GetSessionAsync();
        return await repository.GetRtEntityByRtIdAsync<RtContinent>(session, rtId);
    }

    private static async Task<List<OctoObjectId>> ActiveParentsOfAustriaAsync(ITenantRepository repository)
    {
        using var session = await repository.GetSessionAsync();
        session.StartTransaction();
        var edges = await repository.GetRtAssociationsAsync(session,
            new RtEntityId(TestCkIds.RtCkCountryTypeId, Austria),
            RtAssociationExtendedQueryOptions.Create(GraphDirections.Outbound, SystemCkIds.RtCkParentChildRoleId));
        return edges.Items.Select(e => e.TargetRtId).ToList();
    }

    // --- primary key (_id_) ---------------------------------------------------------------------------------

    [Fact]
    public async Task InsertOnly_AfterArchive_WithSameRtId_ReplacesTheArchivedEntityAndActivatesIt()
    {
        var repository = await StartAsync();
        await ImportAsync(repository, ContinentModel(Europe, "Europe"), ImportStrategy.Insert);
        await DeleteAsync(repository, TestCkIds.RtCkContinentTypeId, Europe);

        var archived = await LoadAsync(repository, TestCkIds.RtCkContinentTypeId, Europe);
        Assert.Equal(RtState.Archived, archived!.RtState);

        await ImportAsync(repository, ContinentModel(Europe, "Europe reimported"), ImportStrategy.Insert);

        var restored = (await LoadContinentAsync(repository, Europe))!;
        Assert.Equal("Europe reimported", restored.Name);
        Assert.NotEqual(RtState.Archived, restored.RtState);
        Assert.Null(restored.RtArchivedDateTime);
    }

    [Fact]
    public async Task InsertOnly_WithSameRtIdAsActiveEntity_FailsAndNamesIndexAndState()
    {
        var repository = await StartAsync();
        await ImportAsync(repository, ContinentModel(Europe, "Europe"), ImportStrategy.Insert);

        var exception = await Assert.ThrowsAnyAsync<Exception>(() =>
            ImportAsync(repository, ContinentModel(Europe, "Europe again"), ImportStrategy.Insert));

        Assert.Contains("already exists in state Active", exception.Message);
        Assert.Contains("_id_", exception.Message);
        Assert.Contains(Europe.ToString(), exception.Message);
        Assert.DoesNotContain("E11000", exception.Message);

        // the active entity is untouched
        var stored = (await LoadContinentAsync(repository, Europe))!;
        Assert.Equal("Europe", stored.Name);
    }

    [Fact]
    public async Task Upsert_AfterArchive_WithSameRtId_RestoresTheEntityAsActive()
    {
        var repository = await StartAsync();
        await ImportAsync(repository, ContinentModel(Europe, "Europe"), ImportStrategy.Insert);
        await DeleteAsync(repository, TestCkIds.RtCkContinentTypeId, Europe);

        await ImportAsync(repository, ContinentModel(Europe, "Europe upserted"), ImportStrategy.Upsert);

        var restored = (await LoadContinentAsync(repository, Europe))!;
        Assert.Equal("Europe upserted", restored.Name);
        Assert.NotEqual(RtState.Archived, restored.RtState);
        Assert.Null(restored.RtArchivedDateTime);
    }

    [Fact]
    public async Task InsertOnly_AfterErase_WithSameRtId_Succeeds()
    {
        var repository = await StartAsync();
        await ImportAsync(repository, ContinentModel(Europe, "Europe"), ImportStrategy.Insert);
        await DeleteAsync(repository, TestCkIds.RtCkContinentTypeId, Europe, DeleteOptions.Erase);
        Assert.Null(await LoadAsync(repository, TestCkIds.RtCkContinentTypeId, Europe));

        await ImportAsync(repository, ContinentModel(Europe, "Europe after erase"), ImportStrategy.Insert);

        var stored = (await LoadContinentAsync(repository, Europe))!;
        Assert.Equal("Europe after erase", stored.Name);
        Assert.NotEqual(RtState.Archived, stored.RtState);
    }

    // --- associations of the archived entity ------------------------------------------------------------------

    [Theory]
    [InlineData(ImportStrategy.Insert)]
    [InlineData(ImportStrategy.Upsert)]
    public async Task ReimportAfterArchive_ListsTheNamedAssociationExactlyOnceAsActive(ImportStrategy strategy)
    {
        var repository = await StartAsync();
        await ImportAsync(repository, CountryUnderContinentModel(), ImportStrategy.Insert);
        Assert.Equal([Europe], await ActiveParentsOfAustriaAsync(repository));

        // the delete archives the entity AND its edges
        await DeleteAsync(repository, TestCkIds.RtCkCountryTypeId, Austria);
        Assert.Empty(await ActiveParentsOfAustriaAsync(repository));

        // Europe is still active: a plain InsertOnly of the whole seed would hit it, so re-import without it
        var countryOnly = CountryUnderContinentModel();
        countryOnly = countryOnly[..countryOnly.IndexOf("  - rtId: " + Europe, StringComparison.Ordinal)] +
                      countryOnly[countryOnly.IndexOf("  - rtId: " + Austria, StringComparison.Ordinal)..];
        await ImportAsync(repository, countryOnly, strategy);

        Assert.Equal([Europe], await ActiveParentsOfAustriaAsync(repository));
    }

    // N:M role: no to-one replacement step in the import, so the edge handling of the bulk write itself is tested

    private static readonly OctoObjectId CustomerId = new("66803ecf4aa85720dda96aa1");
    private static readonly OctoObjectId MeasuringPointId = new("66803ecf4aa85720dda96aa2");

    private static string CustomerModel(bool withMeasuringPoint) => $$"""
        $schema: https://schemas.meshmakers.cloud/runtime-model.schema.json
        dependencies:
          - Test-1.0.0
        entities:
        {{(withMeasuringPoint ? $"""
          - rtId: {MeasuringPointId}
            ckTypeId: Test/MeasuringPoint
            attributes:
              - id: Test/Name
                value: MP-1
              - id: Test/CounterNumber
                value: C-1
              - id: Test/CounterReading
                value: 1.5
        """ : "")}}
          - rtId: {{CustomerId}}
            ckTypeId: Test/Customer
            attributes:
              - id: Test/Contacts.Name
                value: Customer 1
            associations:
              - roleId: Test/CustomerMeasuringPoint
                targetRtId: {{MeasuringPointId}}
                targetCkTypeId: Test/MeasuringPoint
        """;

    private static async Task<int> ActiveMeasuringPointEdgesAsync(ITenantRepository repository)
    {
        using var session = await repository.GetSessionAsync();
        session.StartTransaction();
        var edges = await repository.GetRtAssociationsAsync(session,
            new RtEntityId(TestCkIds.RtCkCustomerTypeId, CustomerId),
            RtAssociationExtendedQueryOptions.Create(GraphDirections.Outbound,
                TestCkIds.RtCkCustomerMeasuringPointRoleId));
        return edges.Items.Count();
    }

    [Theory]
    [InlineData(ImportStrategy.Insert)]
    [InlineData(ImportStrategy.Upsert)]
    public async Task ReimportAfterArchive_ManyRole_HasExactlyOneActiveEdge(ImportStrategy strategy)
    {
        var repository = await StartAsync();
        await ImportAsync(repository, CustomerModel(true), ImportStrategy.Insert);
        Assert.Equal(1, await ActiveMeasuringPointEdgesAsync(repository));

        await DeleteAsync(repository, TestCkIds.RtCkCustomerTypeId, CustomerId);
        Assert.Equal(0, await ActiveMeasuringPointEdgesAsync(repository));

        await ImportAsync(repository, CustomerModel(false), strategy);

        Assert.Equal(1, await ActiveMeasuringPointEdgesAsync(repository));
    }

    // --- unique CK indexes on a business key -------------------------------------------------------------------

    private const string KeyModelName = "ArchiveKeyModel";
    private const string KeyTypeName = "KeyedItem";
    private const string KeyFieldName = "ItemCode";

    private static CkCompiledModelRoot KeyedModel(IndexTypeDto indexType) => new()
    {
        ModelId = new CkModelId($"{KeyModelName}-1.0.0"),
        Dependencies = [SystemCkIds.CkModelId],
        Attributes =
        [
            new CkAttributeDto
            {
                AttributeId = new CkAttributeId(KeyFieldName), ValueType = AttributeValueTypesDto.String
            }
        ],
        Types =
        [
            new()
            {
                TypeId = new CkTypeId(KeyTypeName),
                DerivedFromCkTypeId = SystemCkIds.CkEntityTypeId,
                IsCollectionRoot = true,
                Attributes =
                [
                    new()
                    {
                        CkAttributeId = new CkId<CkAttributeId>($"{KeyModelName}-1.0.0/{KeyFieldName}"),
                        AttributeName = KeyFieldName
                    }
                ],
                Indexes =
                [
                    new() { IndexType = indexType, Fields = [new() { AttributePaths = [KeyFieldName] }] }
                ]
            }
        ]
    };

    private static string KeyedItemModel(OctoObjectId rtId, string code) => $$"""
        $schema: https://schemas.meshmakers.cloud/runtime-model.schema.json
        dependencies:
          - {{KeyModelName}}-1.0.0
        entities:
          - rtId: {{rtId}}
            ckTypeId: {{KeyModelName}}/{{KeyTypeName}}
            attributes:
              - id: {{KeyModelName}}/{{KeyFieldName}}
                value: {{code}}
        """;

    private async Task<Exception?> ReimportKeyWithOtherRtIdAfterArchiveAsync(IndexTypeDto indexType)
    {
        var systemContext = fixture.GetSystemContext();
        var tenantId = $"IT_{Guid.NewGuid():N}"[..20];
        using (var adminSession = await systemContext.GetAdminSessionAsync())
        {
            adminSession.StartTransaction();
            await systemContext.CreateChildTenantAsync(adminSession, tenantId, tenantId);
            await adminSession.CommitTransactionAsync();
        }

        try
        {
            var tenantContext = await systemContext.GetChildTenantContextAsync(tenantId);
            await tenantContext.ImportCkModelAsync(KeyedModel(indexType));
            var repository = tenantContext.GetTenantRepository();
            var typeId = new RtCkId<CkTypeId>($"{KeyModelName}/{KeyTypeName}");
            await repository.GetCkTypeGraphAsync(typeId);

            var first = OctoObjectId.GenerateNewId();
            var second = OctoObjectId.GenerateNewId();
            await ImportAsync(repository, KeyedItemModel(first, "ITEM-0001"), ImportStrategy.Insert);
            await DeleteAsync(repository, typeId, first);

            try
            {
                await ImportAsync(repository, KeyedItemModel(second, "ITEM-0001"), ImportStrategy.Insert);
                return null;
            }
            catch (Exception e)
            {
                return e;
            }
        }
        finally
        {
            using var cleanupSession = await systemContext.GetAdminSessionAsync();
            cleanupSession.StartTransaction();
            await systemContext.DropChildTenantAsync(cleanupSession, tenantId);
            await cleanupSession.CommitTransactionAsync();
        }
    }

    [Fact]
    public async Task UniqueNotDeleted_ArchivedEntityFreesTheBusinessKey()
    {
        var failure = await ReimportKeyWithOtherRtIdAfterArchiveAsync(IndexTypeDto.UniqueNotDeleted);

        Assert.Null(failure);
    }

    [Fact]
    public async Task PlainUnique_ArchivedEntityStillBlocksTheBusinessKey_AndTheMessageSaysSo()
    {
        var failure = await ReimportKeyWithOtherRtIdAfterArchiveAsync(IndexTypeDto.Unique);

        Assert.NotNull(failure);
        Assert.Contains("unique index", failure.Message);
        Assert.Contains("ITEM-0001", failure.Message);
        Assert.Contains("state Archived", failure.Message);
        Assert.Contains("UniqueNotDeleted", failure.Message);
        Assert.Contains("Erase", failure.Message);
        Assert.DoesNotContain("E11000", failure.Message);
    }
}
