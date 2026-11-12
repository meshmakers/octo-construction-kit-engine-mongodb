using Meshmakers.Octo.ConstructionKit.Contracts;
using Meshmakers.Octo.Runtime.Contracts;
using Meshmakers.Octo.Runtime.Contracts.DataPermissions;
using Meshmakers.Octo.Runtime.Contracts.MongoDb.Repositories;
using Meshmakers.Octo.Runtime.Contracts.Repositories;
using Meshmakers.Octo.Runtime.Contracts.Repositories.Query;
using Meshmakers.Octo.Runtime.Engine.MongoDb.IntegrationTests.Collections;
using Meshmakers.Octo.Runtime.Engine.MongoDb.IntegrationTests.Fixtures;

using TestCkModel.Generated.Test.v1;

using Xunit;

namespace Meshmakers.Octo.Runtime.Engine.MongoDb.IntegrationTests.Repositories.Query;

/// <summary>
///     Count parity under data permissions (AB#8895): the count of the existing item query per caller - a tenant
///     caller without restriction, a manager who sees everything, an employee who sees a subset (owned only), an
///     outsider who sees nothing. Part 1 proves the old behaviour; the count-only tests compare against it.
/// </summary>
[Collection(DataPermissionCollection.Name)]
public class CountParityDataPermissionTests(DataPermissionTestFixture fixture)
{
    internal static readonly RtSecurityContext Management = RtSecurityContext.ForUser("user-m", ["TestManagement"]);
    internal static readonly RtSecurityContext Employee1 = RtSecurityContext.ForUser("user-1", ["TestEmployee"]);
    internal static readonly RtSecurityContext Employee2 = RtSecurityContext.ForUser("user-2", ["TestEmployee"]);
    internal static readonly RtSecurityContext Outsider = RtSecurityContext.ForUser("user-x", ["TestOther"]);

    /// <summary>Caller name, expected count, field filter on Name (null = none).</summary>
    public static TheoryData<string, long, string?> Callers() => new()
    {
        { "System", 3, null },
        { "Management", 3, null },
        { "Employee1", 2, null },
        { "Outsider", 0, null },
        { "Employee1", 1, "Emp1-A" },
        { "Management", 1, "Emp2-C" },
        { "Employee1", 0, "Emp2-C" }
    };

    internal static RtSecurityContext? Caller(string name) => name switch
    {
        "System" => null,
        "Management" => Management,
        "Employee1" => Employee1,
        "Outsider" => Outsider,
        _ => throw new ArgumentOutOfRangeException(nameof(name), name, null)
    };

    internal static RtDataPolicyTable BuildTable()
    {
        var continentTypeId = TestCkIds.RtCkContinentTypeId.SemanticVersionedFullName;
        return new RtDataPolicyTable(
        [
            new RtDataPolicyRule("test.continents", new HashSet<string> { continentTypeId },
                [RtDataAction.Read, RtDataAction.Write, RtDataAction.Delete],
                OwnedOnly: false, AuditOnly: false, new HashSet<string> { "TestManagement" }),
            new RtDataPolicyRule("test.continents", new HashSet<string> { continentTypeId },
                [RtDataAction.Read, RtDataAction.Write],
                OwnedOnly: true, AuditOnly: false, new HashSet<string> { "TestEmployee" })
        ]);
    }

    /// <summary>Three continents: two created by user-1, one by user-2.</summary>
    internal static async Task<ITenantRepository> SeedAsync(DataPermissionTestFixture fixture)
    {
        fixture.Resolver.Table = RtDataPolicyTable.Empty;
        await fixture.ClearCollectionAsync();
        var repository = fixture.GetSystemContext().GetTenantRepository();

        await InsertContinentAsync(repository, Employee1, "Emp1-A");
        await InsertContinentAsync(repository, Employee1, "Emp1-B");
        await InsertContinentAsync(repository, Employee2, "Emp2-C");
        return repository;
    }

    internal static RtEntityQueryOptions Options(string? nameFilter) => nameFilter == null
        ? RtEntityQueryOptions.Create()
        : RtEntityQueryOptions.Create().FieldFilter("Name", FieldFilterOperator.Equals, nameFilter);

    internal static async Task<IOctoSession> OpenSessionAsync(ITenantRepository repository, string caller)
    {
        var securityContext = Caller(caller);
        return securityContext == null
            ? await repository.GetSessionAsync()
            : await repository.GetSessionAsync(securityContext);
    }

    private static async Task InsertContinentAsync(ITenantRepository repository, RtSecurityContext securityContext,
        string name)
    {
        using var session = await repository.GetSessionAsync(securityContext);
        session.StartTransaction();
        var rtContinent = await repository.CreateTransientRtEntityAsync<RtContinent>();
        rtContinent.RtId = OctoObjectId.GenerateNewId();
        rtContinent.Name = name;
        await repository.InsertOneRtEntityAsync(session, rtContinent);
        await session.CommitTransactionAsync();
    }

    [Theory]
    [MemberData(nameof(Callers))]
    public async Task OldItemQuery_CountsWhatTheCallerSees(string caller, long expected, string? nameFilter)
    {
        var repository = await SeedAsync(fixture);
        fixture.Resolver.Table = BuildTable();
        try
        {
            using var session = await OpenSessionAsync(repository, caller);
            session.StartTransaction();
            var unpaged = await repository.GetRtEntitiesByTypeAsync(session, TestCkIds.RtCkContinentTypeId,
                Options(nameFilter));
            var paged = await repository.GetRtEntitiesByTypeAsync(session, TestCkIds.RtCkContinentTypeId,
                Options(nameFilter), 0, 1);
            await session.CommitTransactionAsync();

            Assert.Equal(expected, unpaged.TotalCount);
            Assert.Equal(unpaged.Items.Count(), unpaged.TotalCount);
            Assert.Equal(expected, paged.TotalCount);
        }
        finally
        {
            fixture.Resolver.Table = RtDataPolicyTable.Empty;
        }
    }
}
