using System.Text.Json;

using FluentAssertions;

using Meshmakers.Octo.ConstructionKit.Contracts;
using Meshmakers.Octo.ConstructionKit.Contracts.BlueprintCatalogs;
using Meshmakers.Octo.ConstructionKit.Contracts.BlueprintCatalogs.DataTransferObjects;
using Meshmakers.Octo.Runtime.Contracts.Blueprints;
using Meshmakers.Octo.Runtime.Contracts.Repositories.Query;
using Meshmakers.Octo.Runtime.Engine.MongoDb.IntegrationTests.Fixtures;

using Xunit;
using Meshmakers.Octo.Runtime.Engine.MongoDb.IntegrationTests.Collections;

namespace Meshmakers.Octo.Runtime.Engine.MongoDb.IntegrationTests;

/// <summary>
/// Drives BlueprintMigrationExecutor against a real MongoDB tenant that already
/// has TestBp-1.0.0 applied (Alpha + Beta customers). Every scenario crafts a
/// migration DTO in code, runs it through the executor, and verifies the
/// expected side effects on the tenant.
/// </summary>
[Collection(BlueprintServiceCollection.Name)]
public class BlueprintMigrationExecutorIntegrationTests(BlueprintServiceFixture fixture)
{
    private readonly BlueprintServiceFixture _fixture = fixture;

    private static readonly BlueprintId TestBpV1 = new("TestBp", "1.0.0");
    private static readonly RtCkId<CkTypeId> CustomerCkType = new("Test/Customer");
    private static readonly RtCkId<CkTypeId> ContinentCkType = new("Test/Continent");
    private const string CustomerTypeIdString = "Test/Customer";
    private const string ContinentTypeIdString = "Test/Continent";

    [Fact]
    public async Task ExecuteAsync_UpdateStep_ChangesAttributeOnMatchingEntity()
    {
        var ct = TestContext.Current.CancellationToken;
        var tenantId = await _fixture.CreateTestTenantAsync("mig-update");

        try
        {
            await _fixture.GetBlueprintService().ApplyBlueprintAsync(tenantId, TestBpV1, force: false, ct);

            var migration = new BlueprintMigrationDto
            {
                SourceVersion = "1.0.0",
                TargetVersion = "1.1.0",
                Steps =
                [
                    new MigrationStepDto
                    {
                        StepId = "rename-europe",
                        Action = MigrationActionType.Update,
                        Target = new EntityTargetDto
                        {
                            CkTypeId = ContinentTypeIdString,
                            RtWellKnownName = "Europe",
                            BlueprintSourceOnly = true
                        },
                        Data = JsonSerializer.SerializeToElement(
                            new Dictionary<string, object> { ["Name"] = "Europe (renamed)" })
                    }
                ]
            };

            var executor = _fixture.GetService<IBlueprintMigrationExecutor>();
            var result = await executor.ExecuteAsync(tenantId, migration,
                new BlueprintMigrationExecutionOptions
                {
                    DryRun = false,
                    BlueprintSource = TestBpV1.FullName
                }, ct);

            result.Success.Should().BeTrue($"messages: {string.Join("; ", result.Errors.Concat(result.Warnings))}");
            result.CompletedSteps.Should().Be(1);

            var europe = (await QueryContinentsAsync(tenantId))
                .Single(c => c.RtWellKnownName == "Europe");
            europe.GetAttributeStringValueOrDefault("Name")
                .Should().Be("Europe (renamed)");
        }
        finally
        {
            await _fixture.DropTenantAsync(tenantId);
        }
    }

    [Fact]
    public async Task ExecuteAsync_DeleteStep_ErasesMatchingBlueprintEntity()
    {
        var ct = TestContext.Current.CancellationToken;
        var tenantId = await _fixture.CreateTestTenantAsync("mig-delete");

        try
        {
            await _fixture.GetBlueprintService().ApplyBlueprintAsync(tenantId, TestBpV1, force: false, ct);

            var migration = new BlueprintMigrationDto
            {
                SourceVersion = "1.0.0",
                TargetVersion = "1.1.0",
                Steps =
                [
                    new MigrationStepDto
                    {
                        StepId = "delete-beta",
                        Action = MigrationActionType.Delete,
                        Target = new EntityTargetDto
                        {
                            CkTypeId = CustomerTypeIdString,
                            RtWellKnownName = "Beta",
                            BlueprintSourceOnly = true
                        }
                    }
                ]
            };

            var executor = _fixture.GetService<IBlueprintMigrationExecutor>();
            var result = await executor.ExecuteAsync(tenantId, migration,
                new BlueprintMigrationExecutionOptions
                {
                    DryRun = false,
                    BlueprintSource = TestBpV1.FullName
                }, ct);

            result.Success.Should().BeTrue();

            var names = (await QueryCustomersAsync(tenantId))
                .Select(c => c.RtWellKnownName).ToList();
            names.Should().Contain("Alpha");
            names.Should().NotContain("Beta");
        }
        finally
        {
            await _fixture.DropTenantAsync(tenantId);
        }
    }

    [Fact]
    public async Task ExecuteAsync_DryRun_DoesNotMutateTenant()
    {
        var ct = TestContext.Current.CancellationToken;
        var tenantId = await _fixture.CreateTestTenantAsync("mig-dry");

        try
        {
            await _fixture.GetBlueprintService().ApplyBlueprintAsync(tenantId, TestBpV1, force: false, ct);

            var migration = new BlueprintMigrationDto
            {
                SourceVersion = "1.0.0",
                TargetVersion = "1.1.0",
                Steps =
                [
                    new MigrationStepDto
                    {
                        StepId = "would-delete-alpha",
                        Action = MigrationActionType.Delete,
                        Target = new EntityTargetDto
                        {
                            CkTypeId = CustomerTypeIdString,
                            RtWellKnownName = "Alpha",
                            BlueprintSourceOnly = true
                        }
                    }
                ]
            };

            var executor = _fixture.GetService<IBlueprintMigrationExecutor>();
            var result = await executor.ExecuteAsync(tenantId, migration,
                new BlueprintMigrationExecutionOptions
                {
                    DryRun = true,
                    BlueprintSource = TestBpV1.FullName
                }, ct);

            result.Success.Should().BeTrue();

            var customers = await QueryCustomersAsync(tenantId);
            customers.Should().HaveCount(2, "DryRun must not erase anything");
        }
        finally
        {
            await _fixture.DropTenantAsync(tenantId);
        }
    }

    [Fact]
    public async Task ExecuteAsync_TransformSetValue_OverwritesAttribute()
    {
        var ct = TestContext.Current.CancellationToken;
        var tenantId = await _fixture.CreateTestTenantAsync("mig-set");

        try
        {
            await _fixture.GetBlueprintService().ApplyBlueprintAsync(tenantId, TestBpV1, force: false, ct);

            var migration = new BlueprintMigrationDto
            {
                SourceVersion = "1.0.0",
                TargetVersion = "1.1.0",
                Steps =
                [
                    new MigrationStepDto
                    {
                        StepId = "stamp-continent-name",
                        Action = MigrationActionType.Transform,
                        Target = new EntityTargetDto
                        {
                            CkTypeId = ContinentTypeIdString,
                            BlueprintSourceOnly = true
                        },
                        Transform = new TransformConfigDto
                        {
                            Type = TransformType.SetValue,
                            TargetAttribute = "Name",
                            Value = "Standardised"
                        }
                    }
                ]
            };

            var executor = _fixture.GetService<IBlueprintMigrationExecutor>();
            var result = await executor.ExecuteAsync(tenantId, migration,
                new BlueprintMigrationExecutionOptions
                {
                    DryRun = false,
                    BlueprintSource = TestBpV1.FullName
                }, ct);

            result.Success.Should().BeTrue();

            foreach (var continent in await QueryContinentsAsync(tenantId))
            {
                continent.GetAttributeStringValueOrDefault("Name")
                    .Should().Be("Standardised");
            }
        }
        finally
        {
            await _fixture.DropTenantAsync(tenantId);
        }
    }

    [Fact]
    public async Task ExecuteAsync_PreconditionUnmet_FailsBeforeMutation()
    {
        var ct = TestContext.Current.CancellationToken;
        var tenantId = await _fixture.CreateTestTenantAsync("mig-pre");

        try
        {
            await _fixture.GetBlueprintService().ApplyBlueprintAsync(tenantId, TestBpV1, force: false, ct);

            var migration = new BlueprintMigrationDto
            {
                SourceVersion = "1.0.0",
                TargetVersion = "1.1.0",
                PreConditions =
                [
                    new MigrationConditionDto
                    {
                        Type = MigrationConditionType.EntityNotExists,
                        Target = new EntityTargetDto
                        {
                            CkTypeId = CustomerTypeIdString,
                            RtWellKnownName = "Alpha"
                        }
                    }
                ],
                Steps =
                [
                    new MigrationStepDto
                    {
                        StepId = "would-not-run",
                        Action = MigrationActionType.Delete,
                        Target = new EntityTargetDto
                        {
                            CkTypeId = CustomerTypeIdString,
                            RtWellKnownName = "Beta",
                            BlueprintSourceOnly = true
                        }
                    }
                ]
            };

            var executor = _fixture.GetService<IBlueprintMigrationExecutor>();
            var result = await executor.ExecuteAsync(tenantId, migration,
                new BlueprintMigrationExecutionOptions
                {
                    DryRun = false,
                    BlueprintSource = TestBpV1.FullName
                }, ct);

            result.Success.Should().BeFalse("Alpha exists, so EntityNotExists precondition fails");
            (await QueryCustomersAsync(tenantId)).Should().HaveCount(2, "no mutation when precondition is unmet");
        }
        finally
        {
            await _fixture.DropTenantAsync(tenantId);
        }
    }

    [Fact]
    public async Task ExecuteAsync_PostValidationFailure_IsReportedAsError()
    {
        var ct = TestContext.Current.CancellationToken;
        var tenantId = await _fixture.CreateTestTenantAsync("mig-post");

        try
        {
            await _fixture.GetBlueprintService().ApplyBlueprintAsync(tenantId, TestBpV1, force: false, ct);

            var migration = new BlueprintMigrationDto
            {
                SourceVersion = "1.0.0",
                TargetVersion = "1.1.0",
                Steps =
                [
                    new MigrationStepDto
                    {
                        StepId = "noop",
                        Action = MigrationActionType.Update,
                        Target = new EntityTargetDto
                        {
                            CkTypeId = CustomerTypeIdString,
                            RtWellKnownName = "Alpha",
                            BlueprintSourceOnly = true
                        },
                        Data = JsonSerializer.SerializeToElement(new Dictionary<string, object>())
                    }
                ],
                PostValidations =
                [
                    new MigrationValidationDto
                    {
                        ValidationId = "expect-3-customers",
                        Type = MigrationValidationType.EntityCount,
                        Target = new EntityTargetDto { CkTypeId = CustomerTypeIdString },
                        ExpectedCount = 3,
                        Severity = MigrationValidationSeverity.Error
                    }
                ]
            };

            var executor = _fixture.GetService<IBlueprintMigrationExecutor>();
            var result = await executor.ExecuteAsync(tenantId, migration,
                new BlueprintMigrationExecutionOptions
                {
                    DryRun = false,
                    BlueprintSource = TestBpV1.FullName
                }, ct);

            result.Success.Should().BeFalse();
            result.Errors.Should().Contain(e => e.Contains("expect-3-customers"));
        }
        finally
        {
            await _fixture.DropTenantAsync(tenantId);
        }
    }

    private async Task<List<Meshmakers.Octo.Runtime.Contracts.RepositoryEntities.RtEntity>> QueryCustomersAsync(string tenantId)
    {
        var repo = await _fixture.GetRuntimeRepositoryProvider().GetRepositoryAsync(tenantId);
        var session = await repo!.GetSessionAsync();
        var rs = await repo.GetRtEntitiesByTypeAsync(session, CustomerCkType, RtEntityQueryOptions.Create());
        return rs.Items.ToList();
    }

    private async Task<List<Meshmakers.Octo.Runtime.Contracts.RepositoryEntities.RtEntity>> QueryContinentsAsync(string tenantId)
    {
        var repo = await _fixture.GetRuntimeRepositoryProvider().GetRepositoryAsync(tenantId);
        var session = await repo!.GetSessionAsync();
        var rs = await repo.GetRtEntitiesByTypeAsync(session, ContinentCkType, RtEntityQueryOptions.Create());
        return rs.Items.ToList();
    }
}
