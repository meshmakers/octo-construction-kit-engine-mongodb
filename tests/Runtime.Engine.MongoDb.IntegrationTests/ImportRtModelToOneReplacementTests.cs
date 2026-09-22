using Meshmakers.Octo.ConstructionKit.Contracts;
using Meshmakers.Octo.ConstructionKit.Models.System.Generated.System.v2;
using Meshmakers.Octo.Runtime.Contracts;
using Meshmakers.Octo.Runtime.Contracts.Exchange;
using Meshmakers.Octo.Runtime.Contracts.Repositories.Query;
using Meshmakers.Octo.Runtime.Engine.MongoDb.IntegrationTests.Collections;
using Meshmakers.Octo.Runtime.Engine.MongoDb.IntegrationTests.Fixtures;

using TestCkModel.Generated.Test.v1;

using Xunit;

namespace Meshmakers.Octo.Runtime.Engine.MongoDb.IntegrationTests;

/// <summary>
/// End-to-end coverage of the to-one replacement on RT import (AB#5296) against a real MongoDB:
/// the bulk association write upserts on (role, origin, target), so before the fix an Upsert
/// import that pointed a to-one role at a different target than the tenant held appended a
/// second edge next to the existing one. <c>System/ParentChild</c> is outbound-<c>One</c>, so a
/// country re-imported under another continent must end up with exactly the new parent.
/// </summary>
[Collection(ImportTestCkModelCollection.Name)]
public class ImportRtModelToOneReplacementTests(ImportTestCkModelFixture fixture)
{
    private static readonly OctoObjectId Europe = new("66803ecf4aa85720dda96a97");
    private static readonly OctoObjectId Asia = new("66803ecf4aa85720dda96a90");
    private static readonly OctoObjectId Austria = new("66803ecf4aa85720dda96a98");

    private static string Model(OctoObjectId parentOfAustria) => $$"""
        $schema: https://schemas.meshmakers.cloud/runtime-model.schema.json
        dependencies:
          - Test-1.0.0
        entities:
          - rtId: {{Europe}}
            ckTypeId: Test/Continent
            attributes:
              - id: Test/Name
                value: Europe
          - rtId: {{Asia}}
            ckTypeId: Test/Continent
            attributes:
              - id: Test/Name
                value: Asia
          - rtId: {{Austria}}
            ckTypeId: Test/Country
            attributes:
              - id: Test/Name
                value: Österreich
              - id: Test/ISOCode
                value: AT
            associations:
              - roleId: System/ParentChild
                targetRtId: {{parentOfAustria}}
                targetCkTypeId: Test/Continent
        """;

    private async Task<List<OctoObjectId>> ParentsOfAustriaAsync()
    {
        var tenantRepository = fixture.GetSystemContext().GetTenantRepository();
        using var session = await tenantRepository.GetSessionAsync();
        session.StartTransaction();
        var edges = await tenantRepository.GetRtAssociationsAsync(session,
            new RtEntityId(TestCkIds.RtCkCountryTypeId, Austria),
            RtAssociationExtendedQueryOptions.Create(GraphDirections.Outbound, SystemCkIds.RtCkParentChildRoleId));
        return edges.Items.Select(e => e.TargetRtId).ToList();
    }

    [Fact]
    public async Task Upsert_ToOneRolePointedAtAnotherTarget_ReplacesTheExistingEdge()
    {
        await fixture.ClearCollectionAsync();
        var import = fixture.GetService<IImportRtModelCommand>();
        var repository = fixture.GetSystemContext().GetTenantRepository();
        // ClearCollectionAsync unloads the tenant's CK cache; the repository reloads it lazily on
        // first CK access, but the import command validates the model ranges against the cache
        // directly, so warm it the way any real caller has already done before importing.
        await repository.GetCkTypeGraphAsync(TestCkIds.RtCkCountryTypeId);

        await import.ImportTextAsync(repository, Model(Europe), ImportStrategy.Insert);
        Assert.Equal([Europe], await ParentsOfAustriaAsync());

        // The prod-1 shape: the tenant holds one parent, the re-applied seed says another.
        await import.ImportTextAsync(repository, Model(Asia), ImportStrategy.Upsert);

        Assert.Equal([Asia], await ParentsOfAustriaAsync());
    }

    [Fact]
    public async Task Upsert_ToOneRoleWithSameTarget_KeepsExactlyOneEdge()
    {
        await fixture.ClearCollectionAsync();
        var import = fixture.GetService<IImportRtModelCommand>();
        var repository = fixture.GetSystemContext().GetTenantRepository();
        // ClearCollectionAsync unloads the tenant's CK cache; the repository reloads it lazily on
        // first CK access, but the import command validates the model ranges against the cache
        // directly, so warm it the way any real caller has already done before importing.
        await repository.GetCkTypeGraphAsync(TestCkIds.RtCkCountryTypeId);

        await import.ImportTextAsync(repository, Model(Europe), ImportStrategy.Insert);
        await import.ImportTextAsync(repository, Model(Europe), ImportStrategy.Upsert);

        Assert.Equal([Europe], await ParentsOfAustriaAsync());
    }
}
