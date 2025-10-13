using Meshmakers.Octo.ConstructionKit.Contracts;
using Meshmakers.Octo.Runtime.Contracts.Exchange;
using Meshmakers.Octo.Runtime.Contracts.MongoDb;
using Meshmakers.Octo.Runtime.Contracts.MongoDb.Repositories;
using Meshmakers.Octo.Runtime.Engine.MongoDb.Comparison;
using Meshmakers.Octo.Runtime.Engine.MongoDb.Comparison.Models;
using Meshmakers.Octo.SystematizedData.Persistence.SystemTests.Fixtures;
using Xunit;

using ITenantContext = Meshmakers.Octo.Runtime.Contracts.MongoDb.ITenantContext;
using ITenantRepository = Meshmakers.Octo.Runtime.Contracts.MongoDb.Repositories.ITenantRepository;

namespace Meshmakers.Octo.SystematizedData.Persistence.SystemTests;

/// <summary>
///     Comprehensive test for all tenant comparison functionality
/// </summary>
[Collection("Sequential")]
public class ComprehensiveTenantComparisonTests : IClassFixture<SystemFixture>
{
    private readonly SystemFixture _systemFixture;

    public ComprehensiveTenantComparisonTests(SystemFixture systemFixture)
    {
        _systemFixture = systemFixture;
    }

    [Fact]
    public async Task CompareTenants_WithDifferentModelsAndEntities_ShouldDetectAllDifferences()
    {
        // Arrange
        string sourceTenantId = "CompTestSource";
        string targetTenantId = "CompTestTarget";

        try
        {
            ISystemContext systemContext = _systemFixture.GetSystemContext();

            // Create source tenant
            using (IOctoAdminSession session = await systemContext.GetAdminSessionAsync())
            {
                session.StartTransaction();
                await systemContext.CreateChildTenantAsync(session, sourceTenantId, sourceTenantId);
                await session.CommitTransactionAsync();
            }

            // Create target tenant
            using (IOctoAdminSession session = await systemContext.GetAdminSessionAsync())
            {
                session.StartTransaction();
                await systemContext.CreateChildTenantAsync(session, targetTenantId, targetTenantId);
                await session.CommitTransactionAsync();
            }

            // Get tenant contexts
            ITenantContext sourceTenantContext = await systemContext.GetChildTenantContextAsync(sourceTenantId);
            ITenantRepository sourceTenantRepo = sourceTenantContext.GetTenantRepository();

            ITenantContext targetTenantContext = await systemContext.GetChildTenantContextAsync(targetTenantId);
            ITenantRepository targetTenantRepo = targetTenantContext.GetTenantRepository();

            // Import Test CK model to source tenant
            OperationResult sourceOpResult = new();
            await sourceTenantContext.ImportCkModelAsync(new CkModelId("Test-1.0.0"), sourceOpResult);
            Assert.False(sourceOpResult.HasErrors);

            // Import Test CK model to target tenant
            OperationResult targetOpResult = new();
            await targetTenantContext.ImportCkModelAsync(new CkModelId("Test-1.0.0"), targetOpResult);
            Assert.False(targetOpResult.HasErrors);

            // Create source tenant entities
            string sourceEntitiesYaml = @"$schema: https://schemas.meshmakers.cloud/runtime-model.schema.json
dependencies:
  - Test-1.0.0
entities:
  - rtId: 66803ecf4aa85720dda96a97
    ckTypeId: Test/Continent
    attributes:
      - id: Test/Name
        value: Europe
  - rtId: 66803ecf4aa85720dda96a98
    ckTypeId: Test/Country
    attributes:
      - id: Test/Name
        value: Austria
      - id: Test/ISOCode
        value: AT
    associations:
      - roleId: System/ParentChild
        targetRtId: 66803ecf4aa85720dda96a97
        targetCkTypeId: Test/Continent
  - rtId: 66803ecf4aa85720dda96a99
    ckTypeId: Test/StateOrProvince
    attributes:
      - id: Test/Name
        value: Salzburg
    associations:
      - roleId: System/ParentChild
        targetRtId: 66803ecf4aa85720dda96a98
        targetCkTypeId: Test/Country
  - rtId: 66803ecf4aa85720dda96b01
    ckTypeId: Test/District
    attributes:
      - id: Test/Name
        value: Pinzgau
    associations:
      - roleId: System/ParentChild
        targetRtId: 66803ecf4aa85720dda96a99
        targetCkTypeId: Test/StateOrProvince
  - rtId: 66803ecf4aa85720dda96b07
    ckTypeId: Test/Municipality
    attributes:
      - id: Test/Name
        value: Fusch
    associations:
      - roleId: System/ParentChild
        targetRtId: 66803ecf4aa85720dda96b01
        targetCkTypeId: Test/District
  - rtId: 66803ecf4aa85720dda96b09
    ckTypeId: Test/HouseHold
    attributes:
      - id: Test/Name
        value: Zeller Fusch 153
    associations:
      - roleId: System/ParentChild
        targetRtId: 66803ecf4aa85720dda96b07
        targetCkTypeId: Test/Municipality
  - rtId: 66803ecf4aa85720dda96b13
    ckTypeId: Test/Customer
    rtWellKnownName: TestCustomer1
    attributes:
      - id: Test/Contacts.Name
        value:
          ckRecordId: Test/ContactName
          attributes:
            - id: Test/Contacts.FirstName
              value: Max
            - id: Test/Contacts.LastName
              value: Mustermann
      - id: Test/Contacts.Address
        value:
          ckRecordId: Test/ContactAddress
          attributes:
            - id: Test/Contacts.Street
              value: Hauptstraße 1
            - id: Test/Contacts.PostalCode
              value: 5020
            - id: Test/Contacts.City
              value: Salzburg
            - id: Test/Contacts.Country
              value: Austria
  - rtId: 66803ecf4aa85720dda96b11
    ckTypeId: Test/MeasuringPoint
    attributes:
      - id: Test/Name
        value: Main Counter
      - id: Test/CounterNumber
        value: AT123
      - id: Test/CounterReading
        value: 14567
      - id: Test/Tags
        value: [Electricity]
    associations:
      - roleId: System/ParentChild
        targetRtId: 66803ecf4aa85720dda96b09
        targetCkTypeId: Test/HouseHold
      - roleId: System/Related
        targetRtId: 66803ecf4aa85720dda96b13
        targetCkTypeId: Test/Customer
";

            // Create target tenant entities (some same, some different, some missing)
            string targetEntitiesYaml = @"$schema: https://schemas.meshmakers.cloud/runtime-model.schema.json
dependencies:
  - Test-1.0.0
entities:
  - rtId: 66803ecf4aa85720dda96a97
    ckTypeId: Test/Continent
    attributes:
      - id: Test/Name
        value: Europe
  - rtId: 66803ecf4aa85720dda96a98
    ckTypeId: Test/Country
    attributes:
      - id: Test/Name
        value: Austria
      - id: Test/ISOCode
        value: AT
    associations:
      - roleId: System/ParentChild
        targetRtId: 66803ecf4aa85720dda96a97
        targetCkTypeId: Test/Continent
  - rtId: 66803ecf4aa85720dda96a99
    ckTypeId: Test/StateOrProvince
    attributes:
      - id: Test/Name
        value: Tyrol
    associations:
      - roleId: System/ParentChild
        targetRtId: 66803ecf4aa85720dda96a98
        targetCkTypeId: Test/Country
  - rtId: 66803ecf4aa85720dda96b02
    ckTypeId: Test/District
    attributes:
      - id: Test/Name
        value: Innsbruck
    associations:
      - roleId: System/ParentChild
        targetRtId: 66803ecf4aa85720dda96a99
        targetCkTypeId: Test/StateOrProvince
  - rtId: 66803ecf4aa85720dda96b08
    ckTypeId: Test/Municipality
    attributes:
      - id: Test/Name
        value: Innsbruck Stadt
    associations:
      - roleId: System/ParentChild
        targetRtId: 66803ecf4aa85720dda96b02
        targetCkTypeId: Test/District
  - rtId: 66803ecf4aa85720dda96b13
    ckTypeId: Test/Customer
    rtWellKnownName: TestCustomer1
    attributes:
      - id: Test/Contacts.Name
        value:
          ckRecordId: Test/ContactName
          attributes:
            - id: Test/Contacts.FirstName
              value: Max
            - id: Test/Contacts.LastName
              value: Mueller
      - id: Test/Contacts.Address
        value:
          ckRecordId: Test/ContactAddress
          attributes:
            - id: Test/Contacts.Street
              value: Hauptstraße 2
            - id: Test/Contacts.PostalCode
              value: 6020
            - id: Test/Contacts.City
              value: Innsbruck
            - id: Test/Contacts.Country
              value: Austria
  - rtId: 66803ecf4aa85720dda96b14
    ckTypeId: Test/Customer
    rtWellKnownName: TestCustomer2
    attributes:
      - id: Test/Contacts.Name
        value:
          ckRecordId: Test/ContactName
          attributes:
            - id: Test/Contacts.FirstName
              value: John
            - id: Test/Contacts.LastName
              value: Doe
";

            // Import source entities
            IImportRtModelCommand importCommand = _systemFixture.GetService<IImportRtModelCommand>();
            string sourceTempFile = Path.GetTempFileName();
            await File.WriteAllTextAsync(sourceTempFile, sourceEntitiesYaml, TestContext.Current.CancellationToken);
            try
            {
                await importCommand.ImportAsync(sourceTenantRepo, sourceTempFile, ExchangeMimeTypes.MimeTypeYaml,
                    ImportStrategy.Insert);
            }
            finally
            {
                File.Delete(sourceTempFile);
            }

            // Import target entities
            string targetTempFile = Path.GetTempFileName();
            await File.WriteAllTextAsync(targetTempFile, targetEntitiesYaml, TestContext.Current.CancellationToken);
            try
            {
                await importCommand.ImportAsync(targetTenantRepo, targetTempFile, ExchangeMimeTypes.MimeTypeYaml,
                    ImportStrategy.Insert);
            }
            finally
            {
                File.Delete(targetTempFile);
            }

            // Configure comparison to include all areas
            TenantComparisonOptions options = new()
            {
                Areas = ComparisonAreas.All,
                IncludePropertyDifferences = true,
                IncludeAssociationDifferences = true,
                MaxEntitiesPerType = 1000
            };

            // Act - Compare the tenants
            ITenantComparisonService comparisonService = _systemFixture.GetService<ITenantComparisonService>();
            TenantComparisonReport report = await comparisonService.CompareTenantAsync(
                sourceTenantId.ToLower(),
                targetTenantId.ToLower(),
                options,
                TestContext.Current.CancellationToken);

            // Assert - Report structure
            Assert.NotNull(report);
            Assert.NotNull(report.Metadata);
            Assert.NotNull(report.Summary);

            // Assert - Metadata
            Assert.Equal(sourceTenantId.ToLower(), report.Metadata.SourceTenantId);
            Assert.Equal(targetTenantId.ToLower(), report.Metadata.TargetTenantId);
            Assert.True(report.Metadata.Duration > TimeSpan.Zero);
            Assert.True(report.Metadata.ComparisonDate <= DateTime.UtcNow);

            // Assert - MetadataComparison
            Assert.NotNull(report.MetadataComparison);
            Assert.NotNull(report.MetadataComparison.Source);
            Assert.NotNull(report.MetadataComparison.Target);
            Assert.Equal(sourceTenantId.ToLower(), report.MetadataComparison.Source.TenantId);
            Assert.Equal(targetTenantId.ToLower(), report.MetadataComparison.Target.TenantId);
            Assert.NotNull(report.MetadataComparison.Differences);

            // Assert - CkModelComparison
            Assert.NotNull(report.CkModelComparison);
            Assert.NotNull(report.CkModelComparison.OnlyInSource);
            Assert.NotNull(report.CkModelComparison.OnlyInTarget);
            Assert.NotNull(report.CkModelComparison.InBothSameVersion);
            Assert.NotNull(report.CkModelComparison.VersionDifferences);
            // Both tenants have Test-1.0.0
            Assert.Contains(report.CkModelComparison.InBothSameVersion,
                model => model.Id.ToString().StartsWith("Test"));

            // Assert - CkTypeComparison
            Assert.NotNull(report.CkTypeComparison);
            Assert.NotNull(report.CkTypeComparison.OnlyInSource);
            Assert.NotNull(report.CkTypeComparison.OnlyInTarget);
            Assert.NotNull(report.CkTypeComparison.InBothSame);
            Assert.NotNull(report.CkTypeComparison.Differences);
            // Both tenants have the same CK types from Test model
            Assert.True(report.CkTypeComparison.InBothSame.Count > 0);

            // Assert - RtEntityComparisons
            Assert.NotNull(report.RtEntityComparisons);
            Assert.True(report.RtEntityComparisons.Count > 0);

            // Note: Location subtypes (Continent, Country, StateOrProvince, District, Municipality, HouseHold)
            // are stored in the Location collection and don't appear as separate entries in RtEntityComparisons.
            // Only collection root types appear: Customer, MeasuringPoint, Location, etc.

            // Assert - Customer comparison (one same with differences, one only in target)
            Assert.True(report.RtEntityComparisons.ContainsKey("Test/Customer"));
            RtEntityTypeComparison customerComp = report.RtEntityComparisons["Test/Customer"];
            Assert.Equal(1, customerComp.SourceTotalCount);
            Assert.Equal(2, customerComp.TargetTotalCount);
            // TestCustomer2 only in target
            Assert.Single(customerComp.OnlyInTarget);
            Assert.Empty(customerComp.OnlyInSource);
            // TestCustomer1 exists in both but with different data
            Assert.Single(customerComp.Differences);
            RtEntityDifference customerDiff = customerComp.Differences[0];
            Assert.Equal("TestCustomer1", customerDiff.SourceEntity.RtWellKnownName);
            Assert.Equal("TestCustomer1", customerDiff.TargetEntity.RtWellKnownName);
            Assert.Equal(2, customerDiff.DifferenceCount);
            Assert.Equal(2, customerDiff.PropertyDifferences.Count);
            Assert.Contains(customerDiff.PropertyDifferences, pd => pd.PropertyName == "Name");
            Assert.Contains(customerDiff.PropertyDifferences, pd => pd.PropertyName == "Address");

            // Assert - MeasuringPoint comparison (only in source)
            Assert.True(report.RtEntityComparisons.ContainsKey("Test/MeasuringPoint"));
            RtEntityTypeComparison measuringPointComp = report.RtEntityComparisons["Test/MeasuringPoint"];
            Assert.Equal(1, measuringPointComp.SourceTotalCount);
            Assert.Equal(0, measuringPointComp.TargetTotalCount);
            Assert.Single(measuringPointComp.OnlyInSource);
            Assert.Empty(measuringPointComp.OnlyInTarget);
            Assert.Empty(measuringPointComp.Differences);
            Assert.Equal(0, measuringPointComp.MatchedIdenticalCount);

            // Assert - AssociationComparison (may be null if not included in comparison)
            // The options specify IncludeAssociationDifferences = true, but the comparison
            // service may not populate this if there are no associations to compare or
            // if the feature is not yet implemented

            // Assert - Summary
            Assert.True(report.Summary.TotalDifferences > 0);
            Assert.True(report.Summary.CkModelDifferences >= 0);
            Assert.True(report.Summary.CkTypeDifferences >= 0);
            Assert.True(report.Summary.RtEntityDifferences > 0);
            Assert.True(report.Summary.AssociationDifferences > 0);
            Assert.True(report.Summary.MetadataDifferences >= 0);

            // Additional assertions on total differences
            Assert.True(report.Summary.TotalDifferences > 0,
                "Should detect differences between source and target tenants");

            // Verify that entity type comparisons capture proper counts
            foreach (KeyValuePair<string, RtEntityTypeComparison> kvp in report.RtEntityComparisons)
            {
                RtEntityTypeComparison entityComp = kvp.Value;
                Assert.NotNull(entityComp.CkTypeId);
                Assert.True(entityComp.SourceFilteredCount >= 0);
                Assert.True(entityComp.TargetFilteredCount >= 0);
                Assert.NotNull(entityComp.OnlyInSource);
                Assert.NotNull(entityComp.OnlyInTarget);
                Assert.NotNull(entityComp.Differences);
                Assert.True(entityComp.MatchedIdenticalCount >= 0);
                // TotalDifferences should be sum of only-in-source, only-in-target, and differences
                Assert.Equal(
                    entityComp.OnlyInSource.Count + entityComp.OnlyInTarget.Count + entityComp.Differences.Count,
                    entityComp.TotalDifferences);
            }
            
            // Verify AssociationComparison present.
            Assert.NotNull(report.AssociationComparison);
            

            // Verify association comparison integrity
            Assert.True(report.AssociationComparison.TotalDifferences ==
                        report.AssociationComparison.OnlyInSource.Count +
                        report.AssociationComparison.OnlyInTarget.Count);

            // Verify options were applied correctly
            Assert.True(options.IncludePropertyDifferences);
            Assert.True(options.IncludeAssociationDifferences);

            // Final verification - all major comparison areas have been exercised
            Assert.True(report.CkModelComparison.InBothSameVersion.Count +
                        report.CkModelComparison.VersionDifferences.Count +
                        report.CkModelComparison.OnlyInSource.Count +
                        report.CkModelComparison.OnlyInTarget.Count > 0,
                "CkModel comparison should have results");

            Assert.True(report.CkTypeComparison.InBothSame.Count +
                        report.CkTypeComparison.Differences.Count +
                        report.CkTypeComparison.OnlyInSource.Count +
                        report.CkTypeComparison.OnlyInTarget.Count > 0,
                "CkType comparison should have results");

            Assert.True(report.RtEntityComparisons.Count > 0,
                "RtEntity comparison should have results for multiple types");

            Assert.True(report.AssociationComparison.MatchedAssociationCount +
                        report.AssociationComparison.TotalDifferences > 0,
                "Association comparison should have results");
        }
        finally
        {
            // Cleanup: Delete test tenants
            try
            {
                ISystemContext systemContext = _systemFixture.GetSystemContext();

                using (IOctoAdminSession session = await systemContext.GetAdminSessionAsync())
                {
                    session.StartTransaction();
                    bool exists = await systemContext.IsChildTenantExistingAsync(session, sourceTenantId);
                    if (exists)
                    {
                        await systemContext.DropChildTenantAsync(session, sourceTenantId);
                    }

                    await session.CommitTransactionAsync();
                }

                using (IOctoAdminSession session = await systemContext.GetAdminSessionAsync())
                {
                    session.StartTransaction();
                    bool exists = await systemContext.IsChildTenantExistingAsync(session, targetTenantId);
                    if (exists)
                    {
                        await systemContext.DropChildTenantAsync(session, targetTenantId);
                    }

                    await session.CommitTransactionAsync();
                }
            }
            catch
            {
                // Suppress cleanup errors
            }
        }
    }
}
