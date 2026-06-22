using FakeItEasy;
using Meshmakers.Octo.ConstructionKit.Contracts;
using Meshmakers.Octo.ConstructionKit.Contracts.ModelCatalogs.DataTransferObjects;
using Meshmakers.Octo.ConstructionKit.Contracts.Services;
using Meshmakers.Octo.ConstructionKit.Engine.Serialization;
using Meshmakers.Octo.Runtime.Contracts;
using Meshmakers.Octo.Runtime.Contracts.Blueprints;
using Meshmakers.Octo.Runtime.Contracts.CkModelMigrations;
using Meshmakers.Octo.Runtime.Contracts.MongoDb;
using Meshmakers.Octo.Runtime.Contracts.RepositoryEntities;
using Meshmakers.Octo.Runtime.Engine.CkModelMigrations;
using Meshmakers.Octo.Runtime.Engine.MongoDb.IntegrationTests.Fixtures;

using Microsoft.Extensions.Logging.Abstractions;

using Xunit;
using Meshmakers.Octo.Runtime.Engine.MongoDb.IntegrationTests.Collections;

namespace Meshmakers.Octo.Runtime.Engine.MongoDb.IntegrationTests;

/// <summary>
/// End-to-end coverage for the <see cref="CkMigrationTransformType.WrapScalarInRecord"/>
/// transform action against a real MongoDB instance. Confirms the engine round-trips a
/// scalar-list attribute into a record list through the full migration service pathway —
/// the unit-test corpus in <c>Runtime.Engine.Tests</c> exercises the branching logic with
/// fakes; this fixture proves the same logic works against the real Mongo
/// <c>RewriteAttributeValueForMigrationAsync</c> implementation. The migration script is
/// constructed in-memory rather than authored as a YAML on disk + new TestCkModelV3 — the
/// YAML parser path is independently covered by the unit tests; here we want to focus the
/// integration test on the storage write path.
/// </summary>
[Collection(MigrationSupportCollection.Name)]
public class WrapScalarInRecordMigrationTests(MigrationSupportFixture fixture)
{
    private static readonly RtCkId<CkTypeId> MeasuringPointTypeId = new("Test/MeasuringPoint");
    private static readonly RtCkId<CkRecordId> TagRecordId = new("Test/EMailAddress");
    private static readonly CkModelId SourceModel = new("Test-1.0.0");
    private static readonly CkModelId TargetModel = new("Test-1.0.1");

    [Fact]
    public async Task WrapScalarInRecord_EndToEnd_LiftsScalarListToRecordListInMongo()
    {
        // Arrange: insert a fresh MeasuringPoint with a populated StringArray "Tags" slot.
        var systemContext = fixture.GetSystemContext();
        var repository = systemContext.GetTenantRepository();
        var rtId = OctoObjectId.GenerateNewId();

        using (var setupSession = await repository.GetSessionAsync())
        {
            setupSession.StartTransaction();
            try
            {
                var entity = new RtEntity(MeasuringPointTypeId, rtId);
                entity.SetAttributeRawValue("Name", "wrap-fixture");
                entity.SetAttributeRawValue("Tags", new List<string> { "Electricity", "Water" });
                await repository.InsertOneRtEntityForMigrationAsync(setupSession, MeasuringPointTypeId, entity);
                await setupSession.CommitTransactionAsync();
            }
            catch
            {
                await setupSession.AbortTransactionAsync();
                throw;
            }
        }

        // Build a single-step WrapScalarInRecord script in memory and drive it through the
        // migration service. We feed the script to a fake content provider, but everything
        // else (repository provider, audit trail, parser) is the real DI-resolved object.
        var migrationService = BuildMigrationService(BuildWrapTagsScript());

        // Act
        var result = await migrationService.MigrateAsync(
            systemContext.TenantId, SourceModel, TargetModel,
            new CkMigrationOptions { CreateBackup = false },
            TestContext.Current.CancellationToken);

        // Assert: migration succeeded and the fixture entity was rewritten.
        Assert.True(result.Success, string.Join("; ", result.Errors));
        Assert.True(result.EntitiesUpdated >= 1,
            $"At least the fixture entity should have been rewritten (was {result.EntitiesUpdated})");

        using (var verifySession = await repository.GetSessionAsync())
        {
            verifySession.StartTransaction();
            try
            {
                var (entitiesAfter, _) = await repository.GetRtEntitiesByTypeForMigrationAsync(
                    verifySession, MeasuringPointTypeId);
                var migrated = entitiesAfter.FirstOrDefault(e => e.RtId == rtId);
                Assert.NotNull(migrated);

                // Name attribute survives — only Tags should have been touched.
                Assert.Equal("wrap-fixture", migrated.GetAttributeStringValueOrDefault("Name"));

                var tagsValue = migrated.GetAttributeValueOrDefault("Tags");
                Assert.NotNull(tagsValue);

                var records = ExtractRtRecords(tagsValue).ToList();
                Assert.Equal(2, records.Count);
                Assert.All(records, r =>
                {
                    Assert.Equal(TagRecordId, r.CkRecordId);
                    Assert.Equal("base", r.Attributes["EMailAddressType"]);
                });
                Assert.Equal("Electricity", records[0].Attributes["EMailAddress"]);
                Assert.Equal("Water", records[1].Attributes["EMailAddress"]);

                // Cleanup: remove the fixture entity so re-runs of this test class start clean.
                await repository.DeleteOneRtEntityForMigrationAsync(
                    verifySession, MeasuringPointTypeId, rtId);
                await verifySession.CommitTransactionAsync();
            }
            catch
            {
                await verifySession.AbortTransactionAsync();
                throw;
            }
        }
    }

    [Fact]
    public async Task WrapScalarInRecord_EndToEnd_AlreadyLifted_IsIdempotentNoOp()
    {
        // Arrange: insert an entity whose Tags slot is already in record shape (simulating a
        // tenant that already ran the migration before).
        var systemContext = fixture.GetSystemContext();
        var repository = systemContext.GetTenantRepository();
        var rtId = OctoObjectId.GenerateNewId();

        using (var setupSession = await repository.GetSessionAsync())
        {
            setupSession.StartTransaction();
            try
            {
                var entity = new RtEntity(MeasuringPointTypeId, rtId);
                entity.SetAttributeRawValue("Name", "idempotent-fixture");
                entity.SetAttributeRawValue("Tags", new List<RtRecord>
                {
                    new(TagRecordId, new Dictionary<string, object?>
                    {
                        ["EMailAddress"] = "Electricity",
                        ["EMailAddressType"] = "base",
                    }),
                });
                await repository.InsertOneRtEntityForMigrationAsync(setupSession, MeasuringPointTypeId, entity);
                await setupSession.CommitTransactionAsync();
            }
            catch
            {
                await setupSession.AbortTransactionAsync();
                throw;
            }
        }

        var migrationService = BuildMigrationService(BuildWrapTagsScript());

        // Snapshot rtChangedDateTime so we can prove it didn't move.
        DateTime? rtChangedBefore;
        using (var snapshotSession = await repository.GetSessionAsync())
        {
            snapshotSession.StartTransaction();
            var (entities, _) = await repository.GetRtEntitiesByTypeForMigrationAsync(
                snapshotSession, MeasuringPointTypeId);
            rtChangedBefore = entities.First(e => e.RtId == rtId).RtChangedDateTime;
            await snapshotSession.CommitTransactionAsync();
        }

        // Act
        var result = await migrationService.MigrateAsync(
            systemContext.TenantId, SourceModel, TargetModel,
            new CkMigrationOptions { CreateBackup = false },
            TestContext.Current.CancellationToken);

        // Assert: succeeded; the fixture entity was not touched (rtChangedDateTime stable).
        // result.EntitiesUpdated may be non-zero from OTHER MeasuringPoint entities carrying
        // scalar Tags from sampleRtModel.yaml — we just care that OUR fixture row was left
        // alone, because it was already in record shape.
        Assert.True(result.Success, string.Join("; ", result.Errors));

        using (var verifySession = await repository.GetSessionAsync())
        {
            verifySession.StartTransaction();
            try
            {
                var (entitiesAfter, _) = await repository.GetRtEntitiesByTypeForMigrationAsync(
                    verifySession, MeasuringPointTypeId);
                var stillRecord = entitiesAfter.First(e => e.RtId == rtId);

                Assert.Equal(rtChangedBefore, stillRecord.RtChangedDateTime);

                var tagsValue = stillRecord.GetAttributeValueOrDefault("Tags");
                Assert.NotNull(tagsValue);
                var records = ExtractRtRecords(tagsValue).ToList();
                Assert.Single(records);
                Assert.Equal("Electricity", records[0].Attributes["EMailAddress"]);

                await repository.DeleteOneRtEntityForMigrationAsync(
                    verifySession, MeasuringPointTypeId, rtId);
                await verifySession.CommitTransactionAsync();
            }
            catch
            {
                await verifySession.AbortTransactionAsync();
                throw;
            }
        }
    }

    private static CkMigrationScriptDto BuildWrapTagsScript()
    {
        return new CkMigrationScriptDto
        {
            SourceVersion = SourceModel.Version.ToString(),
            TargetVersion = TargetModel.Version.ToString(),
            Steps =
            [
                new CkMigrationStepDto
                {
                    StepId = "wrap-tags",
                    Action = CkMigrationActionType.Transform,
                    Target = new CkMigrationTargetDto { CkTypeId = "Test/MeasuringPoint" },
                    Transform = new CkMigrationTransformDto
                    {
                        Type = CkMigrationTransformType.WrapScalarInRecord,
                        SourceAttribute = "Tags",
                        TargetRecordCkRecordId = "Test/EMailAddress",
                        RecordValueAttribute = "EMailAddress",
                        RecordDefaults = new Dictionary<string, object>
                        {
                            ["EMailAddressType"] = "base",
                        },
                    },
                    OnConflict = CkMigrationConflictBehavior.Fail,
                },
            ],
        };
    }

    private ICkModelMigrationService BuildMigrationService(CkMigrationScriptDto script)
    {
        // The migration service depends on ICkMigrationContentProvider for finding scripts —
        // we feed the in-memory script through a fake here so we don't have to author a
        // separate TestCkModelV3 with a real YAML on disk just for two assertions. Every
        // other dependency is real: the real Mongo IRuntimeRepositoryProvider, the real
        // audit-trail forwarder (so a regression in the audit pipeline would surface here),
        // the real parser. We pass a no-op backup service because the test runs with
        // CreateBackup = false; supplying a real one would force MongoDump availability in
        // the test environment.
        var fakeContentProvider = A.Fake<ICkMigrationContentProvider>();
        A.CallTo(() => fakeContentProvider.HasMigrationsAsync(TargetModel, A<CancellationToken>._))
            .Returns(true);
        A.CallTo(() => fakeContentProvider.GetMigrationMetaAsync(TargetModel, A<CancellationToken>._))
            .Returns(new CkMigrationMetaDto
            {
                CkModelId = "Test-1.0.1",
                Migrations =
                [
                    new CkMigrationReferenceDto
                    {
                        FromVersion = SourceModel.Version.ToString(),
                        ToVersion = TargetModel.Version.ToString(),
                        ScriptPath = "1.0.0-to-1.0.1.yaml",
                    },
                ],
            });
        A.CallTo(() => fakeContentProvider.GetMigrationAsync(
                TargetModel, SourceModel.Version.ToString(), TargetModel.Version.ToString(),
                A<CancellationToken>._))
            .Returns(script);

        var repositoryProvider = fixture.GetService<IRuntimeRepositoryProvider>();
        var auditTrail = fixture.GetService<ICkModelImportAuditTrail>();
        var catalogService = fixture.GetService<ICatalogService>();
        var backupService = A.Fake<ITenantBackupService>();
        var parser = new CkMigrationParser();

        return new CkModelMigrationService(
            parser,
            backupService,
            fakeContentProvider,
            repositoryProvider,
            catalogService,
            auditTrail,
            NullLogger<CkModelMigrationService>.Instance);
    }

    private static IEnumerable<RtRecord> ExtractRtRecords(object value)
    {
        if (value is IEnumerable<RtRecord> typed)
        {
            return typed;
        }

        if (value is System.Collections.IEnumerable raw)
        {
            return raw.OfType<RtRecord>();
        }

        throw new InvalidOperationException($"Expected an enumerable of RtRecord, got {value.GetType().Name}");
    }
}
