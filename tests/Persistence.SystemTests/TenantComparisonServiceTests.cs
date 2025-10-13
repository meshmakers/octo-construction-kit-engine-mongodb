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

            // Configure comparison options to only compare metadata
            TenantComparisonOptions options = new TenantComparisonOptions
            {
                Areas = ComparisonAreas.Metadata | ComparisonAreas.CkModels | ComparisonAreas.Associations,
                IncludePropertyDifferences = false,
                IncludeAssociationDifferences = false
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
