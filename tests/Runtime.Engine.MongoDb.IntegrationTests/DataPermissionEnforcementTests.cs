using Meshmakers.Octo.ConstructionKit.Contracts;
using Meshmakers.Octo.ConstructionKit.Contracts.Services;
using Meshmakers.Octo.ConstructionKit.Models.System.Generated.System.v2;
using Meshmakers.Octo.Runtime.Contracts;
using Meshmakers.Octo.Runtime.Contracts.DataPermissions;
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
///     End-to-end read/write data-permission enforcement (AB#4973) against real MongoDB: the Or
///     predicate on reads (per type set, TotalCount included), not-found semantics for direct id
///     reads, write/delete checks with ownership verification, and the dormant/audit behaviors.
/// </summary>
[Collection(DataPermissionCollection.Name)]
public class DataPermissionEnforcementTests(DataPermissionTestFixture fixture)
{
    private static readonly RtSecurityContext Management =
        RtSecurityContext.ForUser("user-m", ["TestManagement"]);

    private static readonly RtSecurityContext Employee1 =
        RtSecurityContext.ForUser("user-1", ["TestEmployee"]);

    private static readonly RtSecurityContext Employee2 =
        RtSecurityContext.ForUser("user-2", ["TestEmployee"]);

    private static readonly RtSecurityContext Outsider =
        RtSecurityContext.ForUser("user-x", ["TestOther"]);

    // Deliberately the wire/CLI form (element version elided when 1) — exactly what operators enter
    // as a policy target. The element-versioned RtCkId.FullName here masked the E2E format-mismatch
    // bug (classification silently Open for every real-world target) because both comparison sides
    // came from the same versioned accessor.
    private static string ContinentTypeId => TestCkIds.RtCkContinentTypeId.SemanticVersionedFullName;

    private static RtDataPolicyTable BuildTable(bool auditOnly = false)
    {
        return new RtDataPolicyTable(
        [
            new RtDataPolicyRule("test.continents", new HashSet<string> { ContinentTypeId },
                [RtDataAction.Read, RtDataAction.Write, RtDataAction.Delete],
                OwnedOnly: false, AuditOnly: auditOnly, new HashSet<string> { "TestManagement" }),
            new RtDataPolicyRule("test.continents", new HashSet<string> { ContinentTypeId },
                [RtDataAction.Read, RtDataAction.Write],
                OwnedOnly: true, AuditOnly: auditOnly, new HashSet<string> { "TestEmployee" })
        ]);
    }

    private async Task<(ITenantRepository Repository, OctoObjectId Emp1A, OctoObjectId Emp1B, OctoObjectId Emp2C)>
        SeedAsync()
    {
        fixture.Resolver.Table = RtDataPolicyTable.Empty;
        await fixture.ClearCollectionAsync();
        var tenantRepository = fixture.GetSystemContext().GetTenantRepository();

        var emp1A = await InsertContinentAsync(tenantRepository, Employee1, "Emp1-A");
        var emp1B = await InsertContinentAsync(tenantRepository, Employee1, "Emp1-B");
        var emp2C = await InsertContinentAsync(tenantRepository, Employee2, "Emp2-C");
        return (tenantRepository, emp1A, emp1B, emp2C);
    }

    private static async Task<OctoObjectId> InsertContinentAsync(ITenantRepository tenantRepository,
        RtSecurityContext securityContext, string name)
    {
        using var session = await tenantRepository.GetSessionAsync(securityContext);
        session.StartTransaction();
        var rtContinent = await tenantRepository.CreateTransientRtEntityAsync<RtContinent>();
        rtContinent.RtId = OctoObjectId.GenerateNewId();
        rtContinent.Name = name;
        await tenantRepository.InsertOneRtEntityAsync(session, rtContinent);
        await session.CommitTransactionAsync();
        return rtContinent.RtId;
    }

    private static async Task<IResultSet<RtContinent>> ReadAllAsync(ITenantRepository tenantRepository,
        RtSecurityContext? securityContext)
    {
        using var session = securityContext == null
            ? await tenantRepository.GetSessionAsync()
            : await tenantRepository.GetSessionAsync(securityContext);
        session.StartTransaction();
        var result = await tenantRepository.GetRtEntitiesByTypeAsync<RtContinent>(session,
            RtEntityQueryOptions.Create());
        await session.CommitTransactionAsync();
        return result;
    }

    [Fact]
    public async Task Read_ClassifiesPerCaller()
    {
        var (repository, _, _, _) = await SeedAsync();
        fixture.Resolver.Table = BuildTable();
        try
        {
            var managementResult = await ReadAllAsync(repository, Management);
            Assert.Equal(3, managementResult.Items.Count());

            var employeeResult = await ReadAllAsync(repository, Employee1);
            Assert.Equal(2, employeeResult.Items.Count());
            Assert.All(employeeResult.Items, e => Assert.Equal("user-1", e.RtCreatedBy));

            var outsiderResult = await ReadAllAsync(repository, Outsider);
            Assert.Empty(outsiderResult.Items);

            var systemResult = await ReadAllAsync(repository, null);
            Assert.Equal(3, systemResult.Items.Count());
        }
        finally
        {
            fixture.Resolver.Table = RtDataPolicyTable.Empty;
        }
    }

    [Fact]
    public async Task Read_ForeignEntityById_IsNotFound()
    {
        var (repository, _, _, emp2C) = await SeedAsync();
        fixture.Resolver.Table = BuildTable();
        try
        {
            using var session = await repository.GetSessionAsync(Employee1);
            session.StartTransaction();
            var result = await repository.GetRtEntitiesByIdAsync(session, TestCkIds.RtCkContinentTypeId,
                [emp2C], RtEntityQueryOptions.Create());
            await session.CommitTransactionAsync();
            Assert.Empty(result.Items);
        }
        finally
        {
            fixture.Resolver.Table = RtDataPolicyTable.Empty;
        }
    }

    [Fact]
    public async Task Write_OwnershipIsEnforced()
    {
        var (repository, emp1A, _, emp2C) = await SeedAsync();
        fixture.Resolver.Table = BuildTable();
        try
        {
            // Own entity: update succeeds.
            await UpdateNameAsync(repository, Employee1, emp1A, "Updated-Own");

            // Foreign entity: rejected atomically.
            await Assert.ThrowsAsync<RuntimeRepositoryException>(() =>
                UpdateNameAsync(repository, Employee1, emp2C, "Should-Not-Apply"));

            // Management may update anything.
            await UpdateNameAsync(repository, Management, emp2C, "Updated-By-Mgmt");
        }
        finally
        {
            fixture.Resolver.Table = RtDataPolicyTable.Empty;
        }

        var all = await ReadAllAsync(repository, null);
        var byId = all.Items.ToDictionary(e => e.RtId, e => e.Name);
        Assert.Equal("Updated-Own", byId[emp1A]);
        Assert.Equal("Updated-By-Mgmt", byId[emp2C]);
    }

    [Fact]
    public async Task Delete_ActionGrantIsRequired()
    {
        var (repository, emp1A, _, emp2C) = await SeedAsync();
        fixture.Resolver.Table = BuildTable();
        try
        {
            // Employee has no Delete grant — even on the own entity.
            await Assert.ThrowsAsync<RuntimeRepositoryException>(() =>
                DeleteAsync(repository, Employee1, emp1A));

            // Management deletes fine.
            await DeleteAsync(repository, Management, emp2C);
        }
        finally
        {
            fixture.Resolver.Table = RtDataPolicyTable.Empty;
        }

        var all = await ReadAllAsync(repository, null);
        Assert.DoesNotContain(all.Items, e => e.RtId == emp2C);
        Assert.Contains(all.Items, e => e.RtId == emp1A);
    }

    [Fact]
    public async Task Insert_WithoutWriteGrant_IsDenied()
    {
        var (repository, _, _, _) = await SeedAsync();
        fixture.Resolver.Table = BuildTable();
        try
        {
            await Assert.ThrowsAsync<RuntimeRepositoryException>(() =>
                InsertContinentAsync(repository, Outsider, "Denied"));

            // Owned-only Write grant allows inserts (the entity becomes owned).
            await InsertContinentAsync(repository, Employee1, "Emp1-New");
        }
        finally
        {
            fixture.Resolver.Table = RtDataPolicyTable.Empty;
        }

        var all = await ReadAllAsync(repository, null);
        Assert.Equal(4, all.Items.Count());
    }

    [Fact]
    public async Task AuditOnlyPolicies_DoNotFilterOrBlock()
    {
        var (repository, _, _, emp2C) = await SeedAsync();
        fixture.Resolver.Table = BuildTable(auditOnly: true);
        try
        {
            var outsiderResult = await ReadAllAsync(repository, Outsider);
            Assert.Equal(3, outsiderResult.Items.Count());

            // Writes pass too — violations are only audited.
            await UpdateNameAsync(repository, Employee1, emp2C, "Audit-Allows");
        }
        finally
        {
            fixture.Resolver.Table = RtDataPolicyTable.Empty;
        }
    }

    [Fact]
    public async Task PoliciesOnOtherTypes_LeaveTypeOpen()
    {
        var (repository, _, _, _) = await SeedAsync();
        fixture.Resolver.Table = new RtDataPolicyTable(
        [
            new RtDataPolicyRule("other.permission", new HashSet<string> { "Test/DoesNotExist" },
                [RtDataAction.Read], OwnedOnly: false, AuditOnly: false,
                new HashSet<string> { "TestManagement" })
        ]);
        try
        {
            var outsiderResult = await ReadAllAsync(repository, Outsider);
            Assert.Equal(3, outsiderResult.Items.Count());
        }
        finally
        {
            fixture.Resolver.Table = RtDataPolicyTable.Empty;
        }
    }

    private static async Task UpdateNameAsync(ITenantRepository tenantRepository,
        RtSecurityContext securityContext, OctoObjectId rtId, string name)
    {
        using var session = await tenantRepository.GetSessionAsync(securityContext);
        session.StartTransaction();
        var operationResult = new OperationResult();
        var update = new RtEntity(TestCkIds.RtCkContinentTypeId, rtId,
            new Dictionary<string, object?> { { "Name", name } });
        var entityUpdates = new List<IEntityUpdateInfo<RtEntity>>
        {
            EntityUpdateInfo<RtEntity>.CreateUpdate(new RtEntityId(TestCkIds.RtCkContinentTypeId, rtId), update)
        };
        await tenantRepository.ApplyChangesAsync(session, entityUpdates, operationResult);
        await session.CommitTransactionAsync();
    }

    private static async Task DeleteAsync(ITenantRepository tenantRepository,
        RtSecurityContext securityContext, OctoObjectId rtId)
    {
        using var session = await tenantRepository.GetSessionAsync(securityContext);
        session.StartTransaction();
        var operationResult = new OperationResult();
        var entityUpdates = new List<IEntityUpdateInfo<RtEntity>>
        {
            EntityUpdateInfo<RtEntity>.CreateDelete(new RtEntityId(TestCkIds.RtCkContinentTypeId, rtId))
        };
        await tenantRepository.ApplyChangesAsync(session, entityUpdates, operationResult);
        await session.CommitTransactionAsync();
    }

    /// <summary>
    ///     AB#4986: a ::totalCount/::exists count criterion counts only associations whose other-end
    ///     entity the caller may see. Two countries under one continent, created by different
    ///     employees; the policy protects Country (owned-only). System counts 2, the employee counts
    ///     only their own country.
    /// </summary>
    [Fact]
    public async Task AssociationCountFilter_CountsOnlyVisibleChildren()
    {
        fixture.Resolver.Table = RtDataPolicyTable.Empty;
        await fixture.ClearCollectionAsync();
        var repository = fixture.GetSystemContext().GetTenantRepository();
        var ckCacheService = fixture.GetService<ICkCacheService>();
        var tenantId = fixture.GetSystemContext().TenantId;

        OctoObjectId continentId;
        using (var session = await repository.GetSessionAsync())
        {
            session.StartTransaction();
            var continent = await repository.CreateTransientRtEntityAsync<RtContinent>();
            continent.RtId = OctoObjectId.GenerateNewId();
            continent.Name = "Count-Continent";
            await repository.InsertOneRtEntityAsync(session, continent);
            await session.CommitTransactionAsync();
            continentId = continent.RtId;
        }

        await InsertCountryAsync(repository, Employee1, "Emp1-Country", continentId);
        await InsertCountryAsync(repository, Employee2, "Emp2-Country", continentId);

        var countryTarget = TestCkIds.RtCkCountryTypeId;
        fixture.Resolver.Table = new RtDataPolicyTable(
        [
            new RtDataPolicyRule("test.countries", new HashSet<string> { countryTarget.SemanticVersionedFullName },
                [RtDataAction.Read], OwnedOnly: true, AuditOnly: false, new HashSet<string> { "TestEmployee" })
        ]);
        try
        {
            // System context: both children count — the continent matches count >= 2.
            Assert.Single((await QueryContinentsWithChildCountAsync(repository, ckCacheService, tenantId,
                null, FieldFilterOperator.GreaterEqualThan, 2)).Items);

            // Employee1 sees one visible child: count >= 2 excludes the continent, count >= 1 keeps it.
            Assert.Empty((await QueryContinentsWithChildCountAsync(repository, ckCacheService, tenantId,
                Employee1, FieldFilterOperator.GreaterEqualThan, 2)).Items);
            Assert.Single((await QueryContinentsWithChildCountAsync(repository, ckCacheService, tenantId,
                Employee1, FieldFilterOperator.GreaterEqualThan, 1)).Items);
        }
        finally
        {
            fixture.Resolver.Table = RtDataPolicyTable.Empty;
        }
    }

    private static async Task InsertCountryAsync(ITenantRepository repository,
        RtSecurityContext securityContext, string name, OctoObjectId parentContinentId)
    {
        using var session = await repository.GetSessionAsync(securityContext);
        session.StartTransaction();
        var country = await repository.CreateTransientRtEntityAsync<RtCountry>();
        country.RtId = OctoObjectId.GenerateNewId();
        country.Name = name;
        country.ISOCode = name.Substring(0, 2).ToUpperInvariant();
        // Entity and its (mandatory) parent edge must land in one ApplyChanges — the graph rule
        // engine validates multiplicities per change set.
        var operationResult = new OperationResult();
        await repository.ApplyChangesAsync(session,
            new List<IEntityUpdateInfo<RtEntity>> { EntityUpdateInfo<RtEntity>.CreateInsert(country) },
        [
            AssociationUpdateInfo.CreateInsert(
                new RtEntityId(TestCkIds.RtCkCountryTypeId, country.RtId),
                new RtEntityId(TestCkIds.RtCkContinentTypeId, parentContinentId),
                SystemCkIds.RtCkParentChildRoleId)
        ], operationResult);
        await session.CommitTransactionAsync();
        Assert.False(operationResult.HasErrors);
    }

    /// <summary>
    ///     AB#4978: Test/Ticket declares AssigneeId as its owner attribute — the owned-only predicate
    ///     compares that attribute against the subject instead of the stamped rtCreatedBy, on reads
    ///     and on the write-guard ownership check; Test/EscalationTicket inherits the declaration.
    /// </summary>
    [Fact]
    public async Task OwnerAttribute_ReplacesCreatedByForOwnedOnly()
    {
        fixture.Resolver.Table = RtDataPolicyTable.Empty;
        await fixture.ClearCollectionAsync();
        var repository = fixture.GetSystemContext().GetTenantRepository();

        // All created by Employee2 — creator must NOT matter for the owner-attribute type.
        var assignedTo1 = await InsertTicketAsync(repository, Employee2, "Assigned-1", "user-1");
        var assignedTo2 = await InsertTicketAsync(repository, Employee2, "Assigned-2", "user-2");
        var unassigned = await InsertTicketAsync(repository, Employee2, "Unassigned", null);
        var escalation = await InsertEscalationTicketAsync(repository, Employee2, "Escalation-1", "user-1");

        fixture.Resolver.Table = new RtDataPolicyTable(
        [
            new RtDataPolicyRule("test.tickets",
                new HashSet<string> { TestCkIds.RtCkTicketTypeId.SemanticVersionedFullName },
                [RtDataAction.Read, RtDataAction.Write],
                OwnedOnly: true, AuditOnly: false, new HashSet<string> { "TestEmployee" })
        ]);
        try
        {
            // Reads: Employee1 sees exactly the tickets assigned to user-1 — including the derived
            // EscalationTicket (inherited owner attribute); the unassigned ticket is nobody's.
            var employee1Tickets = await ReadAllTicketsAsync(repository, Employee1);
            Assert.Equal(
                new HashSet<OctoObjectId> { assignedTo1, escalation },
                employee1Tickets.Items.Select(t => t.RtId).ToHashSet());

            var employee2Tickets = await ReadAllTicketsAsync(repository, Employee2);
            var employee2Id = Assert.Single(employee2Tickets.Items).RtId;
            Assert.Equal(assignedTo2, employee2Id);

            // Writes: the assignee may update; the creator (Employee2) may NOT update a ticket
            // that is assigned to someone else; nobody owns the unassigned ticket.
            await UpdateTicketNameAsync(repository, Employee1, assignedTo1, "Updated-By-Assignee");
            await Assert.ThrowsAsync<RuntimeRepositoryException>(() =>
                UpdateTicketNameAsync(repository, Employee2, assignedTo1, "Creator-Must-Not-Win"));
            await Assert.ThrowsAsync<RuntimeRepositoryException>(() =>
                UpdateTicketNameAsync(repository, Employee1, unassigned, "Nobody-Owns-This"));

            // System bypasses as always.
            var systemTickets = await ReadAllTicketsAsync(repository, null);
            Assert.Equal(4, systemTickets.Items.Count());
        }
        finally
        {
            fixture.Resolver.Table = RtDataPolicyTable.Empty;
        }
    }

    /// <summary>
    ///     AB#4978 path semantics: Test/ReviewTask declares the record path Owner.UserId as its owner
    ///     — the read predicate targets the nested BSON path (attributes.owner.attributes.userId) and
    ///     the write-guard resolves the value via the path evaluator.
    /// </summary>
    [Fact]
    public async Task OwnerAttributePath_RecordPath_IsEnforced()
    {
        fixture.Resolver.Table = RtDataPolicyTable.Empty;
        await fixture.ClearCollectionAsync();
        var repository = fixture.GetSystemContext().GetTenantRepository();

        var ownedBy1 = await InsertReviewTaskAsync(repository, "Task-1", "user-1");
        var ownedBy2 = await InsertReviewTaskAsync(repository, "Task-2", "user-2");
        var ownerless = await InsertReviewTaskAsync(repository, "Task-None", null);

        fixture.Resolver.Table = new RtDataPolicyTable(
        [
            new RtDataPolicyRule("test.reviewtasks",
                new HashSet<string> { TestCkIds.RtCkReviewTaskTypeId.SemanticVersionedFullName },
                [RtDataAction.Read, RtDataAction.Write],
                OwnedOnly: true, AuditOnly: false, new HashSet<string> { "TestEmployee" })
        ]);
        try
        {
            using (var session = await repository.GetSessionAsync(Employee1))
            {
                session.StartTransaction();
                var result = await repository.GetRtEntitiesByTypeAsync<RtReviewTask>(session,
                    RtEntityQueryOptions.Create());
                await session.CommitTransactionAsync();
                var visible = Assert.Single(result.Items);
                Assert.Equal(ownedBy1, visible.RtId);
            }

            await UpdateReviewTaskNameAsync(repository, Employee1, ownedBy1, "Updated-By-Owner");
            await Assert.ThrowsAsync<RuntimeRepositoryException>(() =>
                UpdateReviewTaskNameAsync(repository, Employee1, ownedBy2, "Foreign-Owner"));
            await Assert.ThrowsAsync<RuntimeRepositoryException>(() =>
                UpdateReviewTaskNameAsync(repository, Employee1, ownerless, "Nobody-Owns-This"));
        }
        finally
        {
            fixture.Resolver.Table = RtDataPolicyTable.Empty;
        }
    }

    private static async Task<OctoObjectId> InsertReviewTaskAsync(ITenantRepository tenantRepository,
        string name, string? ownerUserId)
    {
        using var session = await tenantRepository.GetSessionAsync();
        session.StartTransaction();
        var task = await tenantRepository.CreateTransientRtEntityAsync<RtReviewTask>();
        task.RtId = OctoObjectId.GenerateNewId();
        task.Name = name;
        if (ownerUserId != null)
        {
            task.Owner = new RtOwnerInfoRecord { UserId = ownerUserId };
        }

        await tenantRepository.InsertOneRtEntityAsync(session, task);
        await session.CommitTransactionAsync();
        return task.RtId;
    }

    private static async Task UpdateReviewTaskNameAsync(ITenantRepository tenantRepository,
        RtSecurityContext securityContext, OctoObjectId rtId, string name)
    {
        using var session = await tenantRepository.GetSessionAsync(securityContext);
        session.StartTransaction();
        var operationResult = new OperationResult();
        var update = new RtEntity(TestCkIds.RtCkReviewTaskTypeId, rtId,
            new Dictionary<string, object?> { { "Name", name } });
        var entityUpdates = new List<IEntityUpdateInfo<RtEntity>>
        {
            EntityUpdateInfo<RtEntity>.CreateUpdate(new RtEntityId(TestCkIds.RtCkReviewTaskTypeId, rtId), update)
        };
        await tenantRepository.ApplyChangesAsync(session, entityUpdates, operationResult);
        await session.CommitTransactionAsync();
    }

    private static async Task<IResultSet<RtTicket>> ReadAllTicketsAsync(ITenantRepository tenantRepository,
        RtSecurityContext? securityContext)
    {
        using var session = securityContext == null
            ? await tenantRepository.GetSessionAsync()
            : await tenantRepository.GetSessionAsync(securityContext);
        session.StartTransaction();
        var result = await tenantRepository.GetRtEntitiesByTypeAsync<RtTicket>(session,
            RtEntityQueryOptions.Create());
        await session.CommitTransactionAsync();
        return result;
    }

    private static async Task<OctoObjectId> InsertTicketAsync(ITenantRepository tenantRepository,
        RtSecurityContext securityContext, string name, string? assigneeId)
    {
        using var session = await tenantRepository.GetSessionAsync(securityContext);
        session.StartTransaction();
        var ticket = await tenantRepository.CreateTransientRtEntityAsync<RtTicket>();
        ticket.RtId = OctoObjectId.GenerateNewId();
        ticket.Name = name;
        ticket.AssigneeId = assigneeId;
        await tenantRepository.InsertOneRtEntityAsync(session, ticket);
        await session.CommitTransactionAsync();
        return ticket.RtId;
    }

    private static async Task<OctoObjectId> InsertEscalationTicketAsync(ITenantRepository tenantRepository,
        RtSecurityContext securityContext, string name, string? assigneeId)
    {
        using var session = await tenantRepository.GetSessionAsync(securityContext);
        session.StartTransaction();
        var ticket = await tenantRepository.CreateTransientRtEntityAsync<RtEscalationTicket>();
        ticket.RtId = OctoObjectId.GenerateNewId();
        ticket.Name = name;
        ticket.AssigneeId = assigneeId;
        await tenantRepository.InsertOneRtEntityAsync(session, ticket);
        await session.CommitTransactionAsync();
        return ticket.RtId;
    }

    private static async Task UpdateTicketNameAsync(ITenantRepository tenantRepository,
        RtSecurityContext securityContext, OctoObjectId rtId, string name)
    {
        using var session = await tenantRepository.GetSessionAsync(securityContext);
        session.StartTransaction();
        var operationResult = new OperationResult();
        var update = new RtEntity(TestCkIds.RtCkTicketTypeId, rtId,
            new Dictionary<string, object?> { { "Name", name } });
        var entityUpdates = new List<IEntityUpdateInfo<RtEntity>>
        {
            EntityUpdateInfo<RtEntity>.CreateUpdate(new RtEntityId(TestCkIds.RtCkTicketTypeId, rtId), update)
        };
        await tenantRepository.ApplyChangesAsync(session, entityUpdates, operationResult);
        await session.CommitTransactionAsync();
    }

    private static async Task<IResultSet<RtEntityGraphItem>> QueryContinentsWithChildCountAsync(
        ITenantRepository repository, ICkCacheService ckCacheService, string tenantId,
        RtSecurityContext? securityContext, FieldFilterOperator countOperator, int comparisonValue)
    {
        var continentGraph = ckCacheService.GetRtCkType(tenantId, TestCkIds.RtCkContinentTypeId);
        var childrenAssociation = continentGraph.Associations.In.All
            .First(a => a.NavigationPropertyName == "Children");

        var pair = new NavigationPair(
            [
                new PathTerm("Children", PathType.Navigation),
                new PathTerm(TestCkIds.RtCkCountryTypeId.GetTypeName(), PathType.TargetCkTypeId)
            ],
            [],
            childrenAssociation.CkRoleId.ToRtCkId(),
            GraphDirections.Inbound,
            TestCkIds.RtCkCountryTypeId)
        {
            AssociationCountFilter = new AssociationCountFilter(countOperator, comparisonValue)
        };

        using var session = securityContext == null
            ? await repository.GetSessionAsync()
            : await repository.GetSessionAsync(securityContext);
        session.StartTransaction();
        var result = await repository.GetRtEntitiesGraphByTypeAsync(session, TestCkIds.RtCkContinentTypeId,
            RtEntityQueryOptions.Create(), [pair]);
        await session.CommitTransactionAsync();
        return result;
    }
}
