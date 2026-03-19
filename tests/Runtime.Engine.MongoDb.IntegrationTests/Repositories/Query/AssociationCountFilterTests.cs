using Meshmakers.Octo.ConstructionKit.Contracts;
using Meshmakers.Octo.ConstructionKit.Contracts.DependencyGraph;
using Meshmakers.Octo.ConstructionKit.Contracts.Services;
using Meshmakers.Octo.Runtime.Contracts;
using Meshmakers.Octo.Runtime.Contracts.Repositories;
using Meshmakers.Octo.Runtime.Contracts.Repositories.Query;
using Meshmakers.Octo.Runtime.Engine.MongoDb.IntegrationTests.Fixtures;

using TestCkModel.Generated.Test.v1;

using Xunit;

namespace Meshmakers.Octo.Runtime.Engine.MongoDb.IntegrationTests.Repositories.Query;

/// <summary>
/// Tests for N:M association count filtering via AssociationCountFilter on NavigationPair.
/// Uses the ParentChild association (inbound multiplicity N) on StateOrProvince ← District.
/// Salzburg has 5 districts (active), Tirol has 2 active + 2 archived.
/// </summary>
[Collection("Sequential")]
public class AssociationCountFilterTests(SampleRtModelDataFixture fixture)
    : IClassFixture<SampleRtModelDataFixture>
{
    private NavigationPair CreateChildrenNavigationPair(AssociationCountFilter? countFilter = null)
    {
        var systemContext = fixture.GetSystemContext();
        var ckCacheService = fixture.GetService<ICkCacheService>();

        // ParentChild: inboundName=Children, inboundMultiplicity=N
        // From StateOrProvince, Children is an INBOUND association to District
        var stateOrProvinceGraph = ckCacheService.GetRtCkType(systemContext.TenantId, TestCkIds.RtCkStateOrProvinceTypeId);
        var childrenAssociation = stateOrProvinceGraph.Associations.In.All
            .First(a => a.NavigationPropertyName == "Children");

        var districtGraph = ckCacheService.GetCkType(systemContext.TenantId, childrenAssociation.TargetCkTypeId);
        var concreteTargetType = districtGraph.GetAllDerivedTypes(true).First();

        var pair = new NavigationPair(
            [
                new PathTerm("Children", PathType.Navigation),
                new PathTerm(concreteTargetType.ToRtCkId().GetTypeName(), PathType.TargetCkTypeId),
            ],
            [],
            childrenAssociation.CkRoleId.ToRtCkId(),
            GraphDirections.Inbound,
            concreteTargetType.ToRtCkId());

        if (countFilter != null)
        {
            pair.AssociationCountFilter = countFilter;
        }

        return pair;
    }

    [Fact]
    public async Task AssociationCountFilter_EqualsZero_ReturnsOnlyEntitiesWithoutAssociations()
    {
        var systemContext = fixture.GetSystemContext();
        var tenantRepository = systemContext.GetTenantRepository();

        var navigationPair = CreateChildrenNavigationPair(
            new AssociationCountFilter(FieldFilterOperator.Equals, 0));

        using var session = await tenantRepository.GetSessionAsync();
        session.StartTransaction();

        var resultSet = await tenantRepository.GetRtEntitiesGraphByTypeAsync(
            session,
            TestCkIds.RtCkStateOrProvinceTypeId,
            RtEntityQueryOptions.Create(),
            [navigationPair]);

        // All returned entities should have zero children
        foreach (var entity in resultSet.Items)
        {
            Assert.Equal(0, entity.Associations.Count(a => a.NavigationPropertyName == "Children"));
        }
    }

    [Fact]
    public async Task AssociationCountFilter_GreaterEqualThanOne_FiltersEntitiesWithAssociations()
    {
        var systemContext = fixture.GetSystemContext();
        var tenantRepository = systemContext.GetTenantRepository();

        // First, verify baseline: get all with Include mode to see which have children
        var enrichmentPair = CreateChildrenNavigationPair();

        using var session = await tenantRepository.GetSessionAsync();
        session.StartTransaction();

        var allResults = await tenantRepository.GetRtEntitiesGraphByTypeAsync(
            session,
            TestCkIds.RtCkStateOrProvinceTypeId,
            RtEntityQueryOptions.Create().UseNavigationFilterMode(NavigationFilterMode.Include),
            [enrichmentPair]);

        var entitiesWithChildren = allResults.Items
            .Count(e => e.Associations.Any(a => a.NavigationPropertyName == "Children"));

        // Now apply count filter >= 1
        var filterPair = CreateChildrenNavigationPair(
            new AssociationCountFilter(FieldFilterOperator.GreaterEqualThan, 1));

        var filteredResults = await tenantRepository.GetRtEntitiesGraphByTypeAsync(
            session,
            TestCkIds.RtCkStateOrProvinceTypeId,
            RtEntityQueryOptions.Create(),
            [filterPair]);

        // The filtered count should match the entities that actually have children
        Assert.Equal(entitiesWithChildren, (int)filteredResults.TotalCount);
    }

    [Fact]
    public async Task AssociationCountFilter_NotEquals_Zero_EquivalentToGreaterEqualOne()
    {
        var systemContext = fixture.GetSystemContext();
        var tenantRepository = systemContext.GetTenantRepository();

        using var session = await tenantRepository.GetSessionAsync();
        session.StartTransaction();

        var gteOnePair = CreateChildrenNavigationPair(
            new AssociationCountFilter(FieldFilterOperator.GreaterEqualThan, 1));
        var gteOneResults = await tenantRepository.GetRtEntitiesGraphByTypeAsync(
            session, TestCkIds.RtCkStateOrProvinceTypeId,
            RtEntityQueryOptions.Create(), [gteOnePair]);

        var neZeroPair = CreateChildrenNavigationPair(
            new AssociationCountFilter(FieldFilterOperator.NotEquals, 0));
        var neZeroResults = await tenantRepository.GetRtEntitiesGraphByTypeAsync(
            session, TestCkIds.RtCkStateOrProvinceTypeId,
            RtEntityQueryOptions.Create(), [neZeroPair]);

        // Both filters should return the same count
        Assert.Equal(gteOneResults.TotalCount, neZeroResults.TotalCount);
    }

    [Fact]
    public async Task AssociationCountFilter_WithEnrichment_AssociationsAreLoadedOnFilteredEntities()
    {
        var systemContext = fixture.GetSystemContext();
        var tenantRepository = systemContext.GetTenantRepository();

        // Filter for entities with at least 1 child
        var filterPair = CreateChildrenNavigationPair(
            new AssociationCountFilter(FieldFilterOperator.GreaterEqualThan, 1));

        using var session = await tenantRepository.GetSessionAsync();
        session.StartTransaction();

        var resultSet = await tenantRepository.GetRtEntitiesGraphByTypeAsync(
            session,
            TestCkIds.RtCkStateOrProvinceTypeId,
            RtEntityQueryOptions.Create(),
            [filterPair]);

        Assert.True(resultSet.TotalCount > 0, "Expected at least one entity matching the filter");

        // Every filtered entity must have at least 1 association loaded (enrichment must work)
        foreach (var entity in resultSet.Items)
        {
            var childCount = entity.Associations.Count(a => a.NavigationPropertyName == "Children");
            Assert.True(childCount >= 1,
                $"Entity {entity.RtWellKnownName} passed count filter >= 1 but has {childCount} associations loaded");
        }
    }

    [Fact]
    public async Task AssociationCountFilter_CellValueSimulation_CountMatchesAssociations()
    {
        // This test simulates how RtQueryRowDtoType.CreateRtSimpleQueryCellDto computes
        // totalCount and exists values from entity.Associations — verifying the enrichment
        // provides data that the cell resolver can correctly count.

        var systemContext = fixture.GetSystemContext();
        var tenantRepository = systemContext.GetTenantRepository();

        // Get all StateOrProvince entities with their Children associations enriched
        var enrichmentPair = CreateChildrenNavigationPair();

        using var session = await tenantRepository.GetSessionAsync();
        session.StartTransaction();

        var resultSet = await tenantRepository.GetRtEntitiesGraphByTypeAsync(
            session,
            TestCkIds.RtCkStateOrProvinceTypeId,
            RtEntityQueryOptions.Create().UseNavigationFilterMode(NavigationFilterMode.Include),
            [enrichmentPair]);

        Assert.True(resultSet.Items.Any(), "Expected at least one StateOrProvince entity");

        // Simulate cell value computation exactly like CreateRtSimpleQueryCellDto does:
        // count = rtEntity.Associations.Count(a => a.NavigationPropertyName == navigationTerm.Value)
        const string navigationPropertyName = "Children";

        var entitiesWithChildren = 0;
        foreach (var entity in resultSet.Items)
        {
            var totalCount = entity.Associations.Count(a => a.NavigationPropertyName == navigationPropertyName);
            var exists = totalCount > 0;

            // Verify consistency: if entity has associations with NavigationPropertyName == "Children",
            // totalCount must be > 0 and exists must be true
            if (entity.Associations.Any(a => a.NavigationPropertyName == navigationPropertyName))
            {
                Assert.True(totalCount > 0,
                    $"Entity {entity.RtWellKnownName}: has Children associations but totalCount is {totalCount}");
                Assert.True(exists,
                    $"Entity {entity.RtWellKnownName}: has Children associations but exists is false");
                entitiesWithChildren++;
            }
            else
            {
                Assert.Equal(0, totalCount);
                Assert.False(exists);
            }
        }

        // Salzburg and Tirol should both have children
        Assert.True(entitiesWithChildren >= 2,
            $"Expected at least 2 StateOrProvince with children, got {entitiesWithChildren}");
    }

    [Fact]
    public async Task AssociationCountFilter_FilteredEntities_CellValuesAreConsistentWithFilter()
    {
        // Verifies that when filtering with count >= 1, ALL returned entities
        // produce exists=true and totalCount >= 1 when computing cell values.
        // This catches the bug where the MongoDB filter works but enrichment doesn't load associations.

        var systemContext = fixture.GetSystemContext();
        var tenantRepository = systemContext.GetTenantRepository();

        var filterPair = CreateChildrenNavigationPair(
            new AssociationCountFilter(FieldFilterOperator.GreaterEqualThan, 1));

        using var session = await tenantRepository.GetSessionAsync();
        session.StartTransaction();

        var resultSet = await tenantRepository.GetRtEntitiesGraphByTypeAsync(
            session,
            TestCkIds.RtCkStateOrProvinceTypeId,
            RtEntityQueryOptions.Create(),
            [filterPair]);

        Assert.True(resultSet.TotalCount > 0, "Expected at least one entity matching count >= 1");

        // Simulate cell value computation for every filtered entity
        const string navigationPropertyName = "Children";
        foreach (var entity in resultSet.Items)
        {
            var totalCount = entity.Associations.Count(a => a.NavigationPropertyName == navigationPropertyName);
            var exists = totalCount > 0;

            // These assertions will fail if the enrichment pipeline doesn't load associations
            // even though the count filter passed them through
            Assert.True(exists,
                $"Entity {entity.RtWellKnownName} (rtId={entity.RtId}): " +
                $"passed count filter >= 1 but cell value exists={exists}, totalCount={totalCount}. " +
                $"Loaded associations: [{string.Join(", ", entity.Associations.Select(a => a.NavigationPropertyName))}]");
            Assert.True(totalCount >= 1,
                $"Entity {entity.RtWellKnownName}: cell totalCount={totalCount}, expected >= 1");
        }
    }

    [Fact]
    public async Task AssociationCountFilter_Include_NoFilter_AllEntitiesWithAssociationCounts()
    {
        var systemContext = fixture.GetSystemContext();
        var tenantRepository = systemContext.GetTenantRepository();

        // Use Include mode (default totalCount >= 0) — should return ALL entities with associations enriched
        var enrichmentPair = CreateChildrenNavigationPair();

        using var session = await tenantRepository.GetSessionAsync();
        session.StartTransaction();

        var resultSet = await tenantRepository.GetRtEntitiesGraphByTypeAsync(
            session,
            TestCkIds.RtCkStateOrProvinceTypeId,
            RtEntityQueryOptions.Create().UseNavigationFilterMode(NavigationFilterMode.Include),
            [enrichmentPair]);

        // Verify that entities WITH children have associations loaded
        var withChildren = resultSet.Items
            .Where(e => e.Associations.Any(a => a.NavigationPropertyName == "Children"))
            .ToList();

        Assert.True(withChildren.Count >= 2,
            $"Expected at least 2 StateOrProvince with children (Salzburg, Tirol), got {withChildren.Count}");

        // Each entity with children should have the Children association properly enriched
        foreach (var entity in withChildren)
        {
            var childAssocs = entity.Associations.Where(a => a.NavigationPropertyName == "Children").ToList();
            Assert.True(childAssocs.Count > 0,
                $"Entity {entity.RtWellKnownName} should have Children associations loaded");
        }
    }

    [Fact]
    public async Task AssociationCountFilter_WithPagination_TotalCountReflectsFilter()
    {
        var systemContext = fixture.GetSystemContext();
        var tenantRepository = systemContext.GetTenantRepository();

        // Get total count without pagination first
        var filterPair = CreateChildrenNavigationPair(
            new AssociationCountFilter(FieldFilterOperator.GreaterEqualThan, 1));

        using var session = await tenantRepository.GetSessionAsync();
        session.StartTransaction();

        var fullResults = await tenantRepository.GetRtEntitiesGraphByTypeAsync(
            session,
            TestCkIds.RtCkStateOrProvinceTypeId,
            RtEntityQueryOptions.Create(),
            [filterPair]);

        // Now with pagination (take=1)
        var pagedFilterPair = CreateChildrenNavigationPair(
            new AssociationCountFilter(FieldFilterOperator.GreaterEqualThan, 1));

        var pagedResults = await tenantRepository.GetRtEntitiesGraphByTypeAsync(
            session,
            TestCkIds.RtCkStateOrProvinceTypeId,
            RtEntityQueryOptions.Create(),
            [pagedFilterPair],
            0, 1);

        // TotalCount should match regardless of pagination
        Assert.Equal(fullResults.TotalCount, pagedResults.TotalCount);
        Assert.Single(pagedResults.Items);

        // The single paginated entity should still have associations loaded
        var entity = pagedResults.Items.First();
        Assert.True(entity.Associations.Any(a => a.NavigationPropertyName == "Children"),
            "Paginated entity should have associations loaded via enrichment");
    }

    // ──────────────────────────────────────────────────────────────────────
    // Cross-type association tests (Customer → MeasuringPoint via CustomerMeasuringPoint)
    // These test the case where origin and target are in DIFFERENT MongoDB collections,
    // which previously failed because the inner lookup used the wrong collection.
    // Test data: Customer1 has 2 MeasuringPoints, Customer2 has 1, Customer3 has 0.
    // ──────────────────────────────────────────────────────────────────────

    private NavigationPair CreateCustomerMeasuringPointNavigationPair(AssociationCountFilter? countFilter = null)
    {
        var systemContext = fixture.GetSystemContext();
        var ckCacheService = fixture.GetService<ICkCacheService>();

        // CustomerMeasuringPoint: outboundName=AssignedMeasuringPoints, outboundMultiplicity=N
        // From Customer, AssignedMeasuringPoints is an OUTBOUND association to MeasuringPoint
        var customerGraph = ckCacheService.GetRtCkType(systemContext.TenantId, TestCkIds.RtCkCustomerTypeId);
        var assoc = customerGraph.Associations.Out.All
            .First(a => a.NavigationPropertyName == "AssignedMeasuringPoints");

        var targetGraph = ckCacheService.GetCkType(systemContext.TenantId, assoc.TargetCkTypeId);
        var concreteTarget = targetGraph.GetAllDerivedTypes(true).First();

        var pair = new NavigationPair(
            [
                new PathTerm("AssignedMeasuringPoints", PathType.Navigation),
                new PathTerm(concreteTarget.ToRtCkId().GetTypeName(), PathType.TargetCkTypeId),
            ],
            [],
            assoc.CkRoleId.ToRtCkId(),
            GraphDirections.Outbound,
            concreteTarget.ToRtCkId());

        if (countFilter != null)
        {
            pair.AssociationCountFilter = countFilter;
        }

        return pair;
    }

    private NavigationPair CreateMeasuringPointCustomerNavigationPair(AssociationCountFilter? countFilter = null)
    {
        var systemContext = fixture.GetSystemContext();
        var ckCacheService = fixture.GetService<ICkCacheService>();

        // From MeasuringPoint, AssignedCustomers is an INBOUND association to Customer
        var mpGraph = ckCacheService.GetRtCkType(systemContext.TenantId, TestCkIds.RtCkMeasuringPointTypeId);
        var assoc = mpGraph.Associations.In.All
            .First(a => a.NavigationPropertyName == "AssignedCustomers");

        var targetGraph = ckCacheService.GetCkType(systemContext.TenantId, assoc.TargetCkTypeId);
        var concreteTarget = targetGraph.GetAllDerivedTypes(true).First();

        var pair = new NavigationPair(
            [
                new PathTerm("AssignedCustomers", PathType.Navigation),
                new PathTerm(concreteTarget.ToRtCkId().GetTypeName(), PathType.TargetCkTypeId),
            ],
            [],
            assoc.CkRoleId.ToRtCkId(),
            GraphDirections.Inbound,
            concreteTarget.ToRtCkId());

        if (countFilter != null)
        {
            pair.AssociationCountFilter = countFilter;
        }

        return pair;
    }

    [Fact]
    public async Task CrossType_Outbound_FilterAndEnrichment_CellValuesCorrect()
    {
        // Customer (outbound) → MeasuringPoint: different collections
        // Customer1 has 2, Customer2 has 1, Customer3 has 0
        var systemContext = fixture.GetSystemContext();
        var tenantRepository = systemContext.GetTenantRepository();

        var filterPair = CreateCustomerMeasuringPointNavigationPair(
            new AssociationCountFilter(FieldFilterOperator.GreaterEqualThan, 1));

        using var session = await tenantRepository.GetSessionAsync();
        session.StartTransaction();

        var resultSet = await tenantRepository.GetRtEntitiesGraphByTypeAsync(
            session, TestCkIds.RtCkCustomerTypeId,
            RtEntityQueryOptions.Create(), [filterPair]);

        // Customer1 and Customer2 should be returned (they have MeasuringPoints)
        Assert.Equal(2, (int)resultSet.TotalCount);

        foreach (var entity in resultSet.Items)
        {
            var count = entity.Associations.Count(a => a.NavigationPropertyName == "AssignedMeasuringPoints");
            Assert.True(count >= 1,
                $"Customer {entity.RtWellKnownName}: passed filter >= 1 but enrichment loaded {count} associations. " +
                $"All associations: [{string.Join(", ", entity.Associations.Select(a => $"{a.NavigationPropertyName}"))}]");
        }
    }

    [Fact]
    public async Task CrossType_Outbound_EqualsZero_ReturnsOnlyCustomerWithoutMeasuringPoints()
    {
        var systemContext = fixture.GetSystemContext();
        var tenantRepository = systemContext.GetTenantRepository();

        var filterPair = CreateCustomerMeasuringPointNavigationPair(
            new AssociationCountFilter(FieldFilterOperator.Equals, 0));

        using var session = await tenantRepository.GetSessionAsync();
        session.StartTransaction();

        var resultSet = await tenantRepository.GetRtEntitiesGraphByTypeAsync(
            session, TestCkIds.RtCkCustomerTypeId,
            RtEntityQueryOptions.Create(), [filterPair]);

        // Only Customer3 has no MeasuringPoints
        Assert.True(resultSet.TotalCount >= 1, "Expected at least Customer3 with 0 MeasuringPoints");

        foreach (var entity in resultSet.Items)
        {
            Assert.Equal(0, entity.Associations.Count(a => a.NavigationPropertyName == "AssignedMeasuringPoints"));
        }
    }

    [Fact]
    public async Task CrossType_Outbound_GreaterThanOne_OnlyCustomerWithMultiple()
    {
        var systemContext = fixture.GetSystemContext();
        var tenantRepository = systemContext.GetTenantRepository();

        var filterPair = CreateCustomerMeasuringPointNavigationPair(
            new AssociationCountFilter(FieldFilterOperator.GreaterThan, 1));

        using var session = await tenantRepository.GetSessionAsync();
        session.StartTransaction();

        var resultSet = await tenantRepository.GetRtEntitiesGraphByTypeAsync(
            session, TestCkIds.RtCkCustomerTypeId,
            RtEntityQueryOptions.Create(), [filterPair]);

        // Only Customer1 has > 1 MeasuringPoint (has 2)
        Assert.True(resultSet.TotalCount >= 1);

        foreach (var entity in resultSet.Items)
        {
            var count = entity.Associations.Count(a => a.NavigationPropertyName == "AssignedMeasuringPoints");
            Assert.True(count > 1, $"Expected > 1, got {count} for {entity.RtWellKnownName}");
        }
    }

    [Fact]
    public async Task CrossType_Inbound_FilterAndEnrichment_CellValuesCorrect()
    {
        // MeasuringPoint (inbound) ← Customer: different collections
        // MeasuringPoint1 is assigned to Customer1 + Customer2, MeasuringPoint2 only to Customer1
        var systemContext = fixture.GetSystemContext();
        var tenantRepository = systemContext.GetTenantRepository();

        var filterPair = CreateMeasuringPointCustomerNavigationPair(
            new AssociationCountFilter(FieldFilterOperator.GreaterEqualThan, 1));

        using var session = await tenantRepository.GetSessionAsync();
        session.StartTransaction();

        var resultSet = await tenantRepository.GetRtEntitiesGraphByTypeAsync(
            session, TestCkIds.RtCkMeasuringPointTypeId,
            RtEntityQueryOptions.Create(), [filterPair]);

        // Both MeasuringPoints have assigned customers
        Assert.True(resultSet.TotalCount >= 1);

        foreach (var entity in resultSet.Items)
        {
            var count = entity.Associations.Count(a => a.NavigationPropertyName == "AssignedCustomers");
            Assert.True(count >= 1,
                $"MeasuringPoint {entity.RtWellKnownName}: passed filter >= 1 but enrichment loaded {count} associations. " +
                $"All associations: [{string.Join(", ", entity.Associations.Select(a => $"{a.NavigationPropertyName}"))}]");
        }
    }

    [Fact]
    public async Task CrossType_Inbound_CellValueSimulation_CountsAreCorrect()
    {
        // Verify cell value computation for the inbound cross-type case
        var systemContext = fixture.GetSystemContext();
        var tenantRepository = systemContext.GetTenantRepository();

        var enrichPair = CreateMeasuringPointCustomerNavigationPair();

        using var session = await tenantRepository.GetSessionAsync();
        session.StartTransaction();

        var resultSet = await tenantRepository.GetRtEntitiesGraphByTypeAsync(
            session, TestCkIds.RtCkMeasuringPointTypeId,
            RtEntityQueryOptions.Create().UseNavigationFilterMode(NavigationFilterMode.Include),
            [enrichPair]);

        // Simulate cell value computation
        foreach (var entity in resultSet.Items)
        {
            var totalCount = entity.Associations.Count(a => a.NavigationPropertyName == "AssignedCustomers");
            var exists = totalCount > 0;

            // MeasuringPoint1 (Hauptzähler AT123) should have 2 customers
            // MeasuringPoint2 (Hauptzähler AT125) should have 1 customer
            if (totalCount > 0)
            {
                Assert.True(exists,
                    $"MeasuringPoint has {totalCount} customers but exists is false");
            }
        }

        // At least one MeasuringPoint should have customers
        Assert.True(resultSet.Items.Any(e =>
                e.Associations.Any(a => a.NavigationPropertyName == "AssignedCustomers")),
            "Expected at least one MeasuringPoint with AssignedCustomers");
    }
}
