using Meshmakers.Octo.ConstructionKit.Contracts;
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
}
