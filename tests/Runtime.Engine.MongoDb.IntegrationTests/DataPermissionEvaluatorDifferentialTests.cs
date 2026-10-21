using Meshmakers.Octo.ConstructionKit.Contracts;
using Meshmakers.Octo.ConstructionKit.Contracts.Messages;
using Meshmakers.Octo.Runtime.Contracts;
using Meshmakers.Octo.Runtime.Contracts.DataPermissions;
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
///     AB#7035 — differential test: the public <see cref="IRtDataPermissionEvaluator" /> must agree with what a
///     repository session bound to the same <see cref="RtSecurityContext" /> actually experiences, against real
///     MongoDB. Read: the record is returned by the filtered repository iff the evaluator says visible.
///     Write/Delete: the change set is accepted by the write guard iff the evaluator says allowed — for every
///     record, the invisible ones included: since AB#7126 the guard applies the read visibility first, so the
///     evaluator's NotVisible ("write but not visible") is refused by the repository as "not found", with the
///     exception a missing record raises (<see cref="HiddenRecord_IsRefusedExactlyLikeAMissingOne" />). Covers creator-owned (Continent) and owner-attribute-owned (Ticket) types, full and
///     owned-only grants, callers without any grant, a read-only and a write-only role, and the system context.
/// </summary>
[Collection(DataPermissionCollection.Name)]
public class DataPermissionEvaluatorDifferentialTests(DataPermissionTestFixture fixture)
{
    private static readonly RtSecurityContext Management = RtSecurityContext.ForUser("user-m", ["TestManagement"]);
    private static readonly RtSecurityContext Employee1 = RtSecurityContext.ForUser("user-1", ["TestEmployee"]);
    private static readonly RtSecurityContext Employee2 = RtSecurityContext.ForUser("user-2", ["TestEmployee"]);
    private static readonly RtSecurityContext Outsider = RtSecurityContext.ForUser("user-x", ["TestOther"]);
    private static readonly RtSecurityContext Reader = RtSecurityContext.ForUser("user-r", ["TestReader"]);
    private static readonly RtSecurityContext Writer = RtSecurityContext.ForUser("user-w", ["TestWriter"]);

    // null = the system context (a session without a security context)
    private static readonly (string Name, RtSecurityContext? Context)[] Callers =
    [
        ("management", Management), ("employee1", Employee1), ("employee2", Employee2), ("outsider", Outsider),
        ("reader", Reader), ("writer", Writer), ("system", null)
    ];

    private static RtDataPolicyTable BuildTable(bool auditOnly)
    {
        var continent = new HashSet<string> { TestCkIds.RtCkContinentTypeId.SemanticVersionedFullName };
        var ticket = new HashSet<string> { TestCkIds.RtCkTicketTypeId.SemanticVersionedFullName };
        var all = new[] { RtDataAction.Read, RtDataAction.Write, RtDataAction.Delete };
        var readWrite = new[] { RtDataAction.Read, RtDataAction.Write };
        return new RtDataPolicyTable(
        [
            new RtDataPolicyRule("c.mgmt", continent, all, false, auditOnly, new HashSet<string> { "TestManagement" }),
            new RtDataPolicyRule("c.emp", continent, readWrite, true, auditOnly, new HashSet<string> { "TestEmployee" }),
            new RtDataPolicyRule("c.read", continent, [RtDataAction.Read], false, auditOnly,
                new HashSet<string> { "TestReader" }),
            new RtDataPolicyRule("c.write", continent, [RtDataAction.Write], false, auditOnly,
                new HashSet<string> { "TestWriter" }),
            new RtDataPolicyRule("c.writer-read", continent, [RtDataAction.Read], true, auditOnly,
                new HashSet<string> { "TestWriter" }),
            new RtDataPolicyRule("t.mgmt", ticket, all, false, auditOnly, new HashSet<string> { "TestManagement" }),
            new RtDataPolicyRule("t.emp", ticket, readWrite, true, auditOnly, new HashSet<string> { "TestEmployee" })
        ]);
    }

    private sealed record Target(string Label, RtCkId<CkTypeId> CkTypeId, OctoObjectId RtId);

    private async Task<(ITenantRepository Repository, List<Target> Targets)> SeedAsync()
    {
        fixture.Resolver.Table = RtDataPolicyTable.Empty;
        await fixture.ClearCollectionAsync();
        var repository = fixture.GetSystemContext().GetTenantRepository();
        var targets = new List<Target>
        {
            await InsertContinentAsync(repository, Employee1, "continent-own-by-emp1"),
            await InsertContinentAsync(repository, Employee2, "continent-own-by-emp2"),
            await InsertContinentAsync(repository, Management, "continent-own-by-mgmt"),
            // Ticket owner = AssigneeId (not the creator): creators are deliberately misleading.
            await InsertTicketAsync(repository, Employee2, "ticket-assigned-emp1", "user-1"),
            await InsertTicketAsync(repository, Employee1, "ticket-assigned-emp2", "user-2"),
            await InsertTicketAsync(repository, Employee1, "ticket-unassigned", null)
        };
        return (repository, targets);
    }

    [Theory]
    [InlineData(RtDataAction.Read)]
    [InlineData(RtDataAction.Write)]
    [InlineData(RtDataAction.Delete)]
    public async Task Evaluator_AgreesWithTheRepository_ForEveryCallerAndRecord(RtDataAction action)
    {
        var evaluator = fixture.GetService<IRtDataPermissionEvaluator>();
        var comparisons = 0;
        try
        {
            foreach (var (callerName, context) in Callers)
            {
                // A fresh seed per caller: a successful delete or write must not change the next caller's input.
                var (repository, targets) = await SeedAsync();
                fixture.Resolver.Table = BuildTable(auditOnly: false);
                var effective = context ?? RtSecurityContext.System;

                foreach (var target in targets)
                {
                    var because = $"{callerName} / {action} / {target.Label}";
                    var verdict = await evaluator.EvaluateAsync(repository, effective,
                        new RtEntityId(target.CkTypeId, target.RtId), action,
                        cancellationToken: TestContext.Current.CancellationToken);
                    var visible = await IsVisibleAsync(repository, context, target);

                    // Visibility is action independent: what the filtered repository hides is NotVisible.
                    Assert.True(visible == (verdict.Decision != RtDataPermissionDecision.NotVisible), because);
                    if (action == RtDataAction.Read)
                    {
                        Assert.True(visible == verdict.IsAllowed, because);
                    }
                    else
                    {
                        var succeeded = await TryChangeAsync(repository, context, target, action);
                        Assert.True(succeeded == verdict.IsAllowed, $"{because}: verdict {verdict}, write {succeeded}");
                        Assert.False(verdict.AuditOnlyViolation, because);
                    }

                    comparisons++;
                }
            }
        }
        finally
        {
            fixture.Resolver.Table = RtDataPolicyTable.Empty;
        }

        Assert.Equal(Callers.Length * 6, comparisons);
    }

    [Fact]
    public async Task AuditOnlyPolicies_RepositoryLetsEverythingThrough_EvaluatorAllowsAndFlags()
    {
        var evaluator = fixture.GetService<IRtDataPermissionEvaluator>();
        var (repository, targets) = await SeedAsync();
        fixture.Resolver.Table = BuildTable(auditOnly: true);
        try
        {
            foreach (var action in new[] { RtDataAction.Read, RtDataAction.Write })
            {
                foreach (var target in targets)
                {
                    var verdict = await evaluator.EvaluateAsync(repository, Outsider,
                        new RtEntityId(target.CkTypeId, target.RtId), action,
                        cancellationToken: TestContext.Current.CancellationToken);

                    Assert.True(verdict.IsAllowed, $"{action} / {target.Label}");
                    Assert.True(verdict.AuditOnlyViolation, $"{action} / {target.Label}");
                    Assert.True(await IsVisibleAsync(repository, Outsider, target), $"{action} / {target.Label}");
                    if (action == RtDataAction.Write)
                    {
                        Assert.True(await TryChangeAsync(repository, Outsider, target, action));
                    }
                }
            }

            var management = await evaluator.EvaluateAsync(repository, Management,
                new RtEntityId(targets[0].CkTypeId, targets[0].RtId), RtDataAction.Write,
                cancellationToken: TestContext.Current.CancellationToken);
            Assert.False(management.AuditOnlyViolation);
        }
        finally
        {
            fixture.Resolver.Table = RtDataPolicyTable.Empty;
        }
    }

    [Fact]
    public async Task TypeLevel_AgreesWithTheRepositoryInsert()
    {
        var evaluator = fixture.GetService<IRtDataPermissionEvaluator>();
        var (repository, _) = await SeedAsync();
        fixture.Resolver.Table = BuildTable(auditOnly: false);
        try
        {
            foreach (var (callerName, context) in Callers)
            {
                var verdict = await evaluator.EvaluateTypeAsync(repository, context ?? RtSecurityContext.System,
                    TestCkIds.RtCkContinentTypeId, RtDataAction.Write,
                    cancellationToken: TestContext.Current.CancellationToken);

                bool inserted;
                try
                {
                    await InsertContinentAsync(repository, context, $"insert-by-{callerName}");
                    inserted = true;
                }
                catch (RuntimeRepositoryException)
                {
                    inserted = false;
                }

                Assert.True(inserted == verdict.IsAllowed, $"{callerName}: verdict {verdict}, insert {inserted}");
            }
        }
        finally
        {
            fixture.Resolver.Table = RtDataPolicyTable.Empty;
        }
    }

    [Theory]
    [InlineData("update")]
    [InlineData("archive")]
    [InlineData("erase")]
    public async Task HiddenRecord_IsRefusedExactlyLikeAMissingOne(string change)
    {
        // AB#7126: a record the caller may not see must not be distinguishable from a record that does not
        // exist - same exception type and same message once the id is normalised. Callers: owned-only read with
        // a full write grant (writer), a caller without any grant (outsider), a foreign owner (employee).
        var (repository, targets) = await SeedAsync();
        fixture.Resolver.Table = BuildTable(auditOnly: false);
        try
        {
            foreach (var (callerName, context) in Callers.Where(c => c.Name is "writer" or "outsider" or "employee2"))
            {
                foreach (var target in targets.Where(t => t.CkTypeId == TestCkIds.RtCkContinentTypeId))
                {
                    if (await IsVisibleAsync(repository, context, target))
                    {
                        continue;
                    }

                    var hidden = await CaptureChangeExceptionAsync(repository, context, target.CkTypeId,
                        target.RtId, change);
                    // The natural path: a caller who may read everything, on an id that does not exist.
                    var missingId = OctoObjectId.GenerateNewId();
                    var missing = await CaptureChangeExceptionAsync(repository, Management, target.CkTypeId,
                        missingId, change);
                    // The same missing id asked by the restricted caller must not differ from a hidden one either.
                    var missingForCaller = await CaptureChangeExceptionAsync(repository, context, target.CkTypeId,
                        missingId, change);

                    var because = $"{callerName} / {change} / {target.Label}";
                    Assert.NotNull(hidden);
                    Assert.NotNull(missing);
                    Assert.NotNull(missingForCaller);
                    foreach (var other in new[] { missing, missingForCaller })
                    {
                        Assert.True(hidden.GetType() == other.GetType(),
                            $"{because}: {hidden.GetType().Name} vs {other.GetType().Name}");
                        Assert.Equal(other.Message.Replace(missingId.ToString(), "<id>"),
                            hidden.Message.Replace(target.RtId.ToString(), "<id>"));
                    }

                    // the hidden record is untouched
                    using var system = await repository.GetSessionAsync();
                    var stored = await repository.GetRtEntityByRtIdAsync(system,
                        new RtEntityId(target.CkTypeId, target.RtId));
                    Assert.NotNull(stored);
                    Assert.NotEqual(RtState.Archived, stored.RtState);
                }
            }
        }
        finally
        {
            fixture.Resolver.Table = RtDataPolicyTable.Empty;
        }
    }

    [Fact]
    public async Task WriteWithoutRead_CanInsert_ButNotUpdateOrDeleteExistingRecords()
    {
        // The repro of AB#7126 in its "no read grant at all" variant: role TestBlind holds Write+Delete only.
        var (repository, targets) = await SeedAsync();
        var blind = RtSecurityContext.ForUser("user-b", ["TestBlind"]);
        var continent = new HashSet<string> { TestCkIds.RtCkContinentTypeId.SemanticVersionedFullName };
        fixture.Resolver.Table = new RtDataPolicyTable(
        [
            new RtDataPolicyRule("c.blind", continent, [RtDataAction.Write, RtDataAction.Delete], false, false,
                new HashSet<string> { "TestBlind" })
        ]);
        try
        {
            var inserted = await InsertContinentAsync(repository, blind, "inserted-by-blind");
            foreach (var target in targets.Where(t => t.CkTypeId == TestCkIds.RtCkContinentTypeId))
            {
                Assert.False(await TryChangeAsync(repository, blind, target, RtDataAction.Write), target.Label);
                Assert.False(await TryChangeAsync(repository, blind, target, RtDataAction.Delete), target.Label);
            }

            var evaluator = fixture.GetService<IRtDataPermissionEvaluator>();
            var verdict = await evaluator.EvaluateAsync(repository, blind,
                new RtEntityId(inserted.CkTypeId, inserted.RtId), RtDataAction.Write,
                cancellationToken: TestContext.Current.CancellationToken);
            Assert.Equal(RtDataPermissionDecision.NotVisible, verdict.Decision);
            Assert.False(await TryChangeAsync(repository, blind, inserted, RtDataAction.Write));
        }
        finally
        {
            fixture.Resolver.Table = RtDataPolicyTable.Empty;
        }
    }

    private static async Task<bool> IsVisibleAsync(ITenantRepository repository, RtSecurityContext? context,
        Target target)
    {
        using var session = context == null
            ? await repository.GetSessionAsync()
            : await repository.GetSessionAsync(context);
        session.StartTransaction();
        var result = await repository.GetRtEntitiesByIdAsync(session, target.CkTypeId, [target.RtId],
            RtEntityQueryOptions.Create());
        await session.CommitTransactionAsync();
        return result.Items.Any();
    }

    private static async Task<bool> TryChangeAsync(ITenantRepository repository, RtSecurityContext? context,
        Target target, RtDataAction action)
    {
        using var session = context == null
            ? await repository.GetSessionAsync()
            : await repository.GetSessionAsync(context);
        session.StartTransaction();
        var id = new RtEntityId(target.CkTypeId, target.RtId);
        IEntityUpdateInfo<RtEntity> change = action == RtDataAction.Delete
            ? EntityUpdateInfo<RtEntity>.CreateDelete(id)
            : EntityUpdateInfo<RtEntity>.CreateUpdate(id, new RtEntity(target.CkTypeId, target.RtId,
                new Dictionary<string, object?> { { "Name", $"changed-{Guid.NewGuid():N}" } }));
        try
        {
            await repository.ApplyChangesAsync(session, [change], new OperationResult());
            await session.CommitTransactionAsync();
            return true;
        }
        catch (Exception e) when (e is RuntimeRepositoryException or EntityNotFoundException)
        {
            return false;
        }
    }

    private static async Task<Exception?> CaptureChangeExceptionAsync(ITenantRepository repository,
        RtSecurityContext? context, RtCkId<CkTypeId> ckTypeId, OctoObjectId rtId, string change)
    {
        using var session = context == null
            ? await repository.GetSessionAsync()
            : await repository.GetSessionAsync(context);
        session.StartTransaction();
        var id = new RtEntityId(ckTypeId, rtId);
        IEntityUpdateInfo<RtEntity> update = change == "update"
            ? EntityUpdateInfo<RtEntity>.CreateUpdate(id, new RtEntity(ckTypeId, rtId,
                new Dictionary<string, object?> { { "Name", $"changed-{Guid.NewGuid():N}" } }))
            : EntityUpdateInfo<RtEntity>.CreateDelete(id);
        try
        {
            await repository.ApplyChangesAsync(session, [update],
                change == "erase" ? DeleteOptions.Erase : DeleteOptions.Default, new OperationResult());
            await session.CommitTransactionAsync();
            return null;
        }
        catch (Exception e)
        {
            return e;
        }
    }

    private static async Task<Target> InsertContinentAsync(ITenantRepository repository, RtSecurityContext? context,
        string name)
    {
        using var session = context == null
            ? await repository.GetSessionAsync()
            : await repository.GetSessionAsync(context);
        session.StartTransaction();
        var continent = await repository.CreateTransientRtEntityAsync<RtContinent>();
        continent.RtId = OctoObjectId.GenerateNewId();
        continent.Name = name;
        await repository.InsertOneRtEntityAsync(session, continent);
        await session.CommitTransactionAsync();
        return new Target(name, TestCkIds.RtCkContinentTypeId, continent.RtId);
    }

    private static async Task<Target> InsertTicketAsync(ITenantRepository repository, RtSecurityContext context,
        string name, string? assigneeId)
    {
        using var session = await repository.GetSessionAsync(context);
        session.StartTransaction();
        var ticket = await repository.CreateTransientRtEntityAsync<RtTicket>();
        ticket.RtId = OctoObjectId.GenerateNewId();
        ticket.Name = name;
        ticket.AssigneeId = assigneeId;
        await repository.InsertOneRtEntityAsync(session, ticket);
        await session.CommitTransactionAsync();
        return new Target(name, TestCkIds.RtCkTicketTypeId, ticket.RtId);
    }
}
