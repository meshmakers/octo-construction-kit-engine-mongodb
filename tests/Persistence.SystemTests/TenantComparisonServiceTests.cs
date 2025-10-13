using Meshmakers.Octo.Runtime.Contracts.MongoDb;
using Meshmakers.Octo.Runtime.Contracts.MongoDb.Repositories;
using Meshmakers.Octo.Runtime.Engine.MongoDb.Comparison;
using Meshmakers.Octo.Runtime.Engine.MongoDb.Comparison.Models;
using Meshmakers.Octo.SystematizedData.Persistence.SystemTests.Fixtures;

using Xunit;

using ITestOutputHelper = Xunit.ITestOutputHelper;

namespace Meshmakers.Octo.SystematizedData.Persistence.SystemTests;

[Collection("Sequential")]
public class TenantComparisonServiceTests : IClassFixture<SystemFixture>
{
    private readonly SystemFixture _systemFixture;
    private readonly ITestOutputHelper _output;

    public TenantComparisonServiceTests(SystemFixture systemFixture, ITestOutputHelper output)
    {
        _systemFixture = systemFixture;
        _output = output;
    }

    [Fact]
    public async Task CompareTwoTenants_ShouldGenerateComparisonReport()
    {
        // Arrange
        string tenant1Id = "ComparisonTestTenant1";
        string tenant2Id = "ComparisonTestTenant2";

        try
        {
            // Create first test tenant
            _output.WriteLine($"Creating tenant: {tenant1Id}");
            ISystemContext systemContext = _systemFixture.GetSystemContext();
            using (IOctoAdminSession session = await systemContext.GetAdminSessionAsync())
            {
                session.StartTransaction();
                await systemContext.CreateChildTenantAsync(session, tenant1Id, tenant1Id);
                await session.CommitTransactionAsync();
            }

            _output.WriteLine($"Created tenant: {tenant1Id}");

            // Create second test tenant
            _output.WriteLine($"Creating tenant: {tenant2Id}");
            using (IOctoAdminSession session = await systemContext.GetAdminSessionAsync())
            {
                session.StartTransaction();
                await systemContext.CreateChildTenantAsync(session, tenant2Id, tenant2Id);
                await session.CommitTransactionAsync();
            }

            _output.WriteLine($"Created tenant: {tenant2Id}");

            // Get the comparison service from DI
            ITenantComparisonService comparisonService = _systemFixture.GetService<ITenantComparisonService>();
            _output.WriteLine("Retrieved ITenantComparisonService from DI");

            // Configure comparison options to compare metadata, models, types, and entities
            TenantComparisonOptions options = new TenantComparisonOptions
            {
                Areas = ComparisonAreas.Metadata | ComparisonAreas.CkModels | ComparisonAreas.CkTypes | ComparisonAreas.RtEntities | ComparisonAreas.Associations,
                IncludePropertyDifferences = true,
                IncludeAssociationDifferences = false,
                MaxEntitiesPerType = 100  // Limit entities per type for testing
            };

            // Act
            _output.WriteLine($"Comparing tenants: {tenant1Id} vs {tenant2Id}");
            TenantComparisonReport report = await comparisonService.CompareTenantAsync(
                tenant1Id.ToLower(),
                tenant2Id.ToLower(),
                options,
                TestContext.Current.CancellationToken);

            // Assert
            Assert.NotNull(report);
            _output.WriteLine("Report generated successfully");

            Assert.NotNull(report.Metadata);
            _output.WriteLine($"Report metadata: ComparisonDate={report.Metadata.ComparisonDate}, Duration={report.Metadata.Duration}");

            Assert.Equal(tenant1Id.ToLower(), report.Metadata.SourceTenantId);
            Assert.Equal(tenant2Id.ToLower(), report.Metadata.TargetTenantId);
            _output.WriteLine($"Tenant IDs verified: Source={report.Metadata.SourceTenantId}, Target={report.Metadata.TargetTenantId}");

            Assert.NotNull(report.Summary);
            _output.WriteLine($"Summary: TotalDifferences={report.Summary.TotalDifferences}, MetadataDifferences={report.Summary.MetadataDifferences}");

            // Verify metadata comparison was performed
            Assert.NotNull(report.MetadataComparison);
            _output.WriteLine("Metadata comparison was performed");

            Assert.NotNull(report.MetadataComparison.Source);
            Assert.NotNull(report.MetadataComparison.Target);
            _output.WriteLine($"Source tenant metadata: TenantId={report.MetadataComparison.Source.TenantId}, DatabaseName={report.MetadataComparison.Source.DatabaseName}");
            _output.WriteLine($"Target tenant metadata: TenantId={report.MetadataComparison.Target.TenantId}, DatabaseName={report.MetadataComparison.Target.DatabaseName}");

            Assert.NotNull(report.MetadataComparison.Differences);
            _output.WriteLine($"Metadata differences count: {report.MetadataComparison.Differences.Count}");

            // Log any differences found
            if (report.MetadataComparison.Differences.Count > 0)
            {
                _output.WriteLine("Differences found:");
                foreach (MetadataDifference difference in report.MetadataComparison.Differences)
                {
                    _output.WriteLine($"  - {difference.FieldName}: {difference.Description}");
                }
            }
            else
            {
                _output.WriteLine("No metadata differences found between tenants (expected for empty tenants)");
            }

            // Verify CkModel comparison was performed
            Assert.NotNull(report.CkModelComparison);
            _output.WriteLine("CkModel comparison was performed");

            _output.WriteLine($"CkModel differences: OnlyInSource={report.CkModelComparison.OnlyInSource.Count}, " +
                              $"OnlyInTarget={report.CkModelComparison.OnlyInTarget.Count}, " +
                              $"InBothSameVersion={report.CkModelComparison.InBothSameVersion.Count}, " +
                              $"VersionDifferences={report.CkModelComparison.VersionDifferences.Count}");

            // Log CkModel differences
            if (report.CkModelComparison.OnlyInSource.Count > 0)
            {
                _output.WriteLine("CkModels only in source:");
                foreach (var model in report.CkModelComparison.OnlyInSource)
                {
                    _output.WriteLine($"  - {model.Id} ({model.ModelState})");
                }
            }

            if (report.CkModelComparison.OnlyInTarget.Count > 0)
            {
                _output.WriteLine("CkModels only in target:");
                foreach (var model in report.CkModelComparison.OnlyInTarget)
                {
                    _output.WriteLine($"  - {model.Id} ({model.ModelState})");
                }
            }

            if (report.CkModelComparison.InBothSameVersion.Count > 0)
            {
                _output.WriteLine("CkModels in both with same version:");
                foreach (var model in report.CkModelComparison.InBothSameVersion)
                {
                    _output.WriteLine($"  - {model.Id}");
                }
            }

            if (report.CkModelComparison.VersionDifferences.Count > 0)
            {
                _output.WriteLine("CkModels with version differences:");
                foreach (var diff in report.CkModelComparison.VersionDifferences)
                {
                    _output.WriteLine($"  - {diff.ModelId}: Source={diff.SourceVersion.Id}, Target={diff.TargetVersion.Id}");
                }
            }

            // Verify CkType comparison was performed
            Assert.NotNull(report.CkTypeComparison);
            _output.WriteLine("CkType comparison was performed");

            _output.WriteLine($"CkType differences: OnlyInSource={report.CkTypeComparison.OnlyInSource.Count}, " +
                              $"OnlyInTarget={report.CkTypeComparison.OnlyInTarget.Count}, " +
                              $"InBothSame={report.CkTypeComparison.InBothSame.Count}, " +
                              $"Differences={report.CkTypeComparison.Differences.Count}");

            // Log CkType differences
            if (report.CkTypeComparison.OnlyInSource.Count > 0)
            {
                _output.WriteLine("CkTypes only in source:");
                foreach (var ckType in report.CkTypeComparison.OnlyInSource)
                {
                    _output.WriteLine($"  - {ckType.CkTypeId}");
                }
            }

            if (report.CkTypeComparison.OnlyInTarget.Count > 0)
            {
                _output.WriteLine("CkTypes only in target:");
                foreach (var ckType in report.CkTypeComparison.OnlyInTarget)
                {
                    _output.WriteLine($"  - {ckType.CkTypeId}");
                }
            }

            if (report.CkTypeComparison.InBothSame.Count > 0)
            {
                _output.WriteLine("CkTypes in both with same properties:");
                foreach (var ckType in report.CkTypeComparison.InBothSame)
                {
                    _output.WriteLine($"  - {ckType.CkTypeId}");
                }
            }

            if (report.CkTypeComparison.Differences.Count > 0)
            {
                _output.WriteLine("CkTypes with differences:");
                foreach (var diff in report.CkTypeComparison.Differences)
                {
                    _output.WriteLine($"  - {diff.CkTypeId}: {diff.Description}");
                }
            }

            // Verify RtEntity comparison was performed
            Assert.NotNull(report.RtEntityComparisons);
            _output.WriteLine("RtEntity comparison was performed");

            _output.WriteLine($"RtEntity comparison results: {report.RtEntityComparisons.Count} CkTypes compared");

            // Log RtEntity comparison details for each CkType
            foreach (var kvp in report.RtEntityComparisons)
            {
                var entityComparison = kvp.Value;
                _output.WriteLine($"CkType {entityComparison.CkTypeId}:");
                _output.WriteLine($"  Source: {entityComparison.SourceTotalCount} total, {entityComparison.SourceFilteredCount} filtered");
                _output.WriteLine($"  Target: {entityComparison.TargetTotalCount} total, {entityComparison.TargetFilteredCount} filtered");
                _output.WriteLine($"  OnlyInSource: {entityComparison.OnlyInSource.Count}");
                _output.WriteLine($"  OnlyInTarget: {entityComparison.OnlyInTarget.Count}");
                _output.WriteLine($"  Differences: {entityComparison.Differences.Count}");
                _output.WriteLine($"  MatchedIdentical: {entityComparison.MatchedIdenticalCount}");
                _output.WriteLine($"  TotalDifferences: {entityComparison.TotalDifferences}");

                // Log matched entities with differences
                if (entityComparison.Differences.Count > 0)
                {
                    _output.WriteLine($"  Entities with differences:");
                    foreach (var diff in entityComparison.Differences)
                    {
                        _output.WriteLine($"    - RtId: {diff.SourceEntity.RtId}, MatchedBy: {diff.MatchedBy}, Differences: {diff.DifferenceCount}");
                        foreach (var propDiff in diff.PropertyDifferences)
                        {
                        _output.WriteLine($"      - {propDiff.PropertyName} ({propDiff.DifferenceType}): {propDiff.SourceValue} -> {propDiff.TargetValue}");
                        }
                    }
                }
            }

            _output.WriteLine("Test completed successfully!");
        }
        finally
        {
            // Cleanup: Delete test tenants
            try
            {
                ISystemContext systemContext = _systemFixture.GetSystemContext();

                _output.WriteLine($"Cleaning up tenant: {tenant1Id}");
                using (IOctoAdminSession session = await systemContext.GetAdminSessionAsync())
                {
                    session.StartTransaction();
                    bool exists = await systemContext.IsChildTenantExistingAsync(session, tenant1Id);
                    if (exists)
                    {
                        await systemContext.DropChildTenantAsync(session, tenant1Id);
                    }
                    await session.CommitTransactionAsync();
                }

                _output.WriteLine($"Cleaning up tenant: {tenant2Id}");
                using (IOctoAdminSession session = await systemContext.GetAdminSessionAsync())
                {
                    session.StartTransaction();
                    bool exists = await systemContext.IsChildTenantExistingAsync(session, tenant2Id);
                    if (exists)
                    {
                        await systemContext.DropChildTenantAsync(session, tenant2Id);
                    }
                    await session.CommitTransactionAsync();
                }

                _output.WriteLine("Cleanup completed successfully");
            }
            catch (Exception ex)
            {
                _output.WriteLine($"Cleanup failed: {ex.Message}");
            }
        }
    }
}
