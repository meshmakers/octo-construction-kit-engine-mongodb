using Meshmakers.Octo.ConstructionKit.Contracts;
using Meshmakers.Octo.ConstructionKit.Contracts.DataTransferObjects;
using Meshmakers.Octo.ConstructionKit.Contracts.DependencyGraph;
using Meshmakers.Octo.Runtime.Contracts.RepositoryEntities;
using Meshmakers.Octo.Runtime.Engine.MongoDb.Comparison;
using Meshmakers.Octo.Runtime.Engine.MongoDb.Comparison.Comparators;
using Meshmakers.Octo.Runtime.Engine.MongoDb.Comparison.Models;

namespace Runtime.Engine.MongoDb.Tests.Comparison;

public class RtEntityComparatorTests
{
    private readonly RtEntityComparator _comparator;
    private readonly TenantComparisonOptions _defaultOptions;

    public RtEntityComparatorTests()
    {
        _comparator = new RtEntityComparator();
        _defaultOptions = new TenantComparisonOptions
        {
            IncludePropertyDifferences = true,
            IncludeAssociationDifferences = false
        };
    }

    #region Empty and Basic Scenarios

    [Fact]
    public void Compare_BothEmpty_ReturnsEmptyResults()
    {
        // Arrange
        var sourceEntities = new Dictionary<string, List<RtEntity>>();
        var targetEntities = new Dictionary<string, List<RtEntity>>();
        var sourceCkTypes = new List<CkTypeGraph>();
        var targetCkTypes = new List<CkTypeGraph>();

        // Act
        Dictionary<string, RtEntityTypeComparison> result = _comparator.Compare(
            sourceEntities, targetEntities, sourceCkTypes, targetCkTypes,
            [], [], _defaultOptions);

        // Assert
        Assert.Empty(result);
    }

    [Fact]
    public void Compare_EntitiesOnlyInSource_AddsToOnlyInSource()
    {
        // Arrange
        string ckTypeId = "CRM/CRM.Customer";
        var sourceEntities = new Dictionary<string, List<RtEntity>>
        {
            [ckTypeId] =
            [
                CreateRtEntitiy(ckTypeId, "customer1", "CustomerA"),
                CreateRtEntitiy(ckTypeId, "customer2", "CustomerB"),
            ]
        };
        var targetEntities = new Dictionary<string, List<RtEntity>>();
        var sourceCkTypes = new List<CkTypeGraph> { CreateCkType("CRM", "Customer") };
        var targetCkTypes = new List<CkTypeGraph>();

        // Act
        Dictionary<string, RtEntityTypeComparison> result = _comparator.Compare(
            sourceEntities, targetEntities, sourceCkTypes, targetCkTypes,
            [], [], _defaultOptions);

        // Assert
        Assert.Single(result);
        Assert.True(result.ContainsKey(ckTypeId));
        Assert.Equal(2, result[ckTypeId].OnlyInSource.Count);
        Assert.Empty(result[ckTypeId].OnlyInTarget);
        Assert.Empty(result[ckTypeId].Differences);
        Assert.Equal(2, result[ckTypeId].TotalDifferences);
    }

    [Fact]
    public void Compare_EntitiesOnlyInTarget_AddsToOnlyInTarget()
    {
        // Arrange
        string ckTypeId = "CRM/CRM.Customer";
        var sourceEntities = new Dictionary<string, List<RtEntity>>();
        var targetEntities = new Dictionary<string, List<RtEntity>>
        {
            [ckTypeId] =
            [
                CreateRtEntitiy(ckTypeId, "customer1", "CustomerA"),
                CreateRtEntitiy(ckTypeId, "customer2", "CustomerB"),
            ]
        };
        var sourceCkTypes = new List<CkTypeGraph>();
        var targetCkTypes = new List<CkTypeGraph> { CreateCkType("CRM", "Customer") };

        // Act
        Dictionary<string, RtEntityTypeComparison> result = _comparator.Compare(
            sourceEntities, targetEntities, sourceCkTypes, targetCkTypes,
            [], [], _defaultOptions);

        // Assert
        Assert.Single(result);
        Assert.True(result.ContainsKey(ckTypeId));
        Assert.Empty(result[ckTypeId].OnlyInSource);
        Assert.Equal(2, result[ckTypeId].OnlyInTarget.Count);
        Assert.Empty(result[ckTypeId].Differences);
        Assert.Equal(2, result[ckTypeId].TotalDifferences);
    }

    #endregion

    #region Matching by RtId

    [Fact]
    public void Compare_SameEntitiesByRtId_MatchesCorrectly()
    {
        // Arrange
        string ckTypeId = "ECommerce/ECommerce.Product";
        string rtId = "507f1f77bcf86cd799439011";

        var sourceEntity = CreateRtEntitiy(ckTypeId, rtId, null);
        var targetEntity = CreateRtEntitiy(ckTypeId, rtId, null);

        var sourceEntities = new Dictionary<string, List<RtEntity>>
        {
            [ckTypeId] = [sourceEntity]
        };
        var targetEntities = new Dictionary<string, List<RtEntity>>
        {
            [ckTypeId] = [targetEntity]
        };
        var sourceCkTypes = new List<CkTypeGraph> { CreateCkType("ECommerce", "Product") };
        var targetCkTypes = new List<CkTypeGraph> { CreateCkType("ECommerce", "Product") };

        // Act
        Dictionary<string, RtEntityTypeComparison> result = _comparator.Compare(
            sourceEntities, targetEntities, sourceCkTypes, targetCkTypes,
            [], [], _defaultOptions);

        // Assert
        Assert.Single(result);
        Assert.Empty(result[ckTypeId].OnlyInSource);
        Assert.Empty(result[ckTypeId].OnlyInTarget);
        Assert.Empty(result[ckTypeId].Differences);
        Assert.Equal(1, result[ckTypeId].MatchedIdenticalCount);
        Assert.Equal(0, result[ckTypeId].TotalDifferences);
    }

    [Fact]
    public void Compare_EntitiesWithDifferentTimestamps_DetectsDifferences()
    {
        // Arrange
        string ckTypeId = "Logistics/Logistics.Shipment";
        string rtId = "507f1f77bcf86cd799439011";

        var sourceEntity = CreateRtEntitiy(ckTypeId, rtId, null,
            createdDate: new DateTime(2025, 1, 1, 10, 0, 0, DateTimeKind.Utc),
            modifiedDate: new DateTime(2025, 1, 2, 10, 0, 0, DateTimeKind.Utc));
        var targetEntity = CreateRtEntitiy(ckTypeId, rtId, null,
            createdDate: new DateTime(2025, 1, 1, 10, 0, 0, DateTimeKind.Utc),
            modifiedDate: new DateTime(2025, 1, 3, 15, 30, 0, DateTimeKind.Utc));

        var sourceEntities = new Dictionary<string, List<RtEntity>>
        {
            [ckTypeId] = [sourceEntity]
        };
        var targetEntities = new Dictionary<string, List<RtEntity>>
        {
            [ckTypeId] = [targetEntity]
        };
        var sourceCkTypes = new List<CkTypeGraph> { CreateCkType("Logistics", "Shipment") };
        var targetCkTypes = new List<CkTypeGraph> { CreateCkType("Logistics", "Shipment") };

        // Act
        Dictionary<string, RtEntityTypeComparison> result = _comparator.Compare(
            sourceEntities, targetEntities, sourceCkTypes, targetCkTypes,
            [], [], _defaultOptions);

        // Assert
        Assert.Single(result);
        Assert.Empty(result[ckTypeId].OnlyInSource);
        Assert.Empty(result[ckTypeId].OnlyInTarget);
        Assert.Single(result[ckTypeId].Differences);
        Assert.Equal(0, result[ckTypeId].MatchedIdenticalCount);

        RtEntityDifference diff = result[ckTypeId].Differences[0];
        Assert.Equal("ByCkTypeIdAndRtId", diff.MatchedBy);
        Assert.Single(diff.PropertyDifferences);
        Assert.Equal("RtChangedDateTime", diff.PropertyDifferences[0].PropertyName);
        Assert.Equal(DifferenceType.Modified, diff.PropertyDifferences[0].DifferenceType);
    }

    #endregion

    #region Matching by WellKnownName

    [Fact]
    public void Compare_MatchByWellKnownName_WhenRtIdsDiffer()
    {
        // Arrange
        string ckTypeId = "Config/Config.SystemConfiguration";
        string wellKnownName = "DefaultEmailConfig";

        var sourceEntity = CreateRtEntitiy(ckTypeId, "507f1f77bcf86cd799439011", wellKnownName);
        var targetEntity = CreateRtEntitiy(ckTypeId, "507f1f77bcf86cd799439022", wellKnownName);

        var sourceEntities = new Dictionary<string, List<RtEntity>>
        {
            [ckTypeId] = [sourceEntity]
        };
        var targetEntities = new Dictionary<string, List<RtEntity>>
        {
            [ckTypeId] = [targetEntity]
        };
        var sourceCkTypes = new List<CkTypeGraph> { CreateCkType("Config", "SystemConfiguration") };
        var targetCkTypes = new List<CkTypeGraph> { CreateCkType("Config", "SystemConfiguration") };

        // Act
        Dictionary<string, RtEntityTypeComparison> result = _comparator.Compare(
            sourceEntities, targetEntities, sourceCkTypes, targetCkTypes,
            [], [], _defaultOptions);

        // Assert
        Assert.Single(result);
        Assert.Empty(result[ckTypeId].OnlyInSource);
        Assert.Empty(result[ckTypeId].OnlyInTarget);
        Assert.Empty(result[ckTypeId].Differences);
        Assert.Equal(1, result[ckTypeId].MatchedIdenticalCount);
    }

    [Fact]
    public void Compare_PreferRtIdOverWellKnownName_WhenBothAvailable()
    {
        // Arrange
        string ckTypeId = "HR/HR.Employee";
        string rtId = "507f1f77bcf86cd799439011";
        string wellKnownName = "JohnDoe";

        // Source has both RtId and WellKnownName
        var sourceEntity = CreateRtEntitiy(ckTypeId, rtId, wellKnownName);

        // Target has same RtId but different WellKnownName
        var targetEntity = CreateRtEntitiy(ckTypeId, rtId, "JaneDoe");

        var sourceEntities = new Dictionary<string, List<RtEntity>>
        {
            [ckTypeId] = [sourceEntity]
        };
        var targetEntities = new Dictionary<string, List<RtEntity>>
        {
            [ckTypeId] = [targetEntity]
        };
        var sourceCkTypes = new List<CkTypeGraph> { CreateCkType("HR", "Employee") };
        var targetCkTypes = new List<CkTypeGraph> { CreateCkType("HR", "Employee") };

        // Act
        Dictionary<string, RtEntityTypeComparison> result = _comparator.Compare(
            sourceEntities, targetEntities, sourceCkTypes, targetCkTypes,
            [], [], _defaultOptions);

        // Assert
        Assert.Single(result);
        Assert.Single(result[ckTypeId].Differences);

        RtEntityDifference diff = result[ckTypeId].Differences[0];
        Assert.Equal("ByCkTypeIdAndRtId", diff.MatchedBy); // Should prefer RtId matching
        Assert.Single(diff.PropertyDifferences);
        Assert.Equal("RtWellKnownName", diff.PropertyDifferences[0].PropertyName);
    }

    #endregion

    #region Attribute Comparison

    [Fact]
    public void Compare_AttributeDifferences_DetectsModifiedAttributes()
    {
        // Arrange
        string ckTypeId = "Sales/Sales.Invoice";
        string rtId = "507f1f77bcf86cd799439011";

        var sourceEntity = CreateRtEntitiy(ckTypeId, rtId, null);
        sourceEntity.SetAttributeValue("InvoiceNumber", AttributeValueTypesDto.String, "INV-2025-001");
        sourceEntity.SetAttributeValue("Amount", AttributeValueTypesDto.Double, 1500.00);
        sourceEntity.SetAttributeValue("Status", AttributeValueTypesDto.String, "Draft");

        var targetEntity = CreateRtEntitiy(ckTypeId, rtId, null);
        targetEntity.SetAttributeValue("InvoiceNumber", AttributeValueTypesDto.String, "INV-2025-001");
        targetEntity.SetAttributeValue("Amount", AttributeValueTypesDto.Double, 1500.00);
        targetEntity.SetAttributeValue("Status", AttributeValueTypesDto.String, "Approved"); // Different!

        var sourceEntities = new Dictionary<string, List<RtEntity>>
        {
            [ckTypeId] = [sourceEntity]
        };
        var targetEntities = new Dictionary<string, List<RtEntity>>
        {
            [ckTypeId] = [targetEntity]
        };

        var ckType = CreateCkType("Sales", "Invoice", attributeNames: ["InvoiceNumber", "Amount", "Status"]);

        var sourceCkTypes = new List<CkTypeGraph> { ckType };
        var targetCkTypes = new List<CkTypeGraph> { ckType };

        // Act
        Dictionary<string, RtEntityTypeComparison> result = _comparator.Compare(
            sourceEntities, targetEntities, sourceCkTypes, targetCkTypes,
            [], [], _defaultOptions);

        // Assert
        Assert.Single(result);
        Assert.Single(result[ckTypeId].Differences);

        RtEntityDifference diff = result[ckTypeId].Differences[0];
        Assert.Single(diff.PropertyDifferences);
        Assert.Equal("Status", diff.PropertyDifferences[0].PropertyName);
        Assert.Equal(DifferenceType.Modified, diff.PropertyDifferences[0].DifferenceType);
        Assert.Equal("Draft", diff.PropertyDifferences[0].SourceValue);
        Assert.Equal("Approved", diff.PropertyDifferences[0].TargetValue);
    }

    [Fact]
    public void Compare_AttributeAddedInTarget_DetectsAddition()
    {
        // Arrange
        string ckTypeId = "Inventory/Inventory.Product";
        string rtId = "507f1f77bcf86cd799439011";

        var sourceEntity = CreateRtEntitiy(ckTypeId, rtId, null);
        sourceEntity.SetAttributeValue("Name", AttributeValueTypesDto.String, "Laptop Computer");
        sourceEntity.SetAttributeValue("Code", AttributeValueTypesDto.String, "LAPTOP-001");

        var targetEntity = CreateRtEntitiy(ckTypeId, rtId, null);
        targetEntity.SetAttributeValue("Name", AttributeValueTypesDto.String, "Laptop Computer");
        targetEntity.SetAttributeValue("Code", AttributeValueTypesDto.String, "LAPTOP-001");
        targetEntity.SetAttributeValue("Description", AttributeValueTypesDto.String, "High-performance laptop"); // Added!

        var sourceEntities = new Dictionary<string, List<RtEntity>>
        {
            [ckTypeId] = [sourceEntity]
        };
        var targetEntities = new Dictionary<string, List<RtEntity>>
        {
            [ckTypeId] = [targetEntity]
        };

        var ckType = CreateCkType("Inventory", "Product", attributeNames: ["Name", "Code", "Description"]);

        var sourceCkTypes = new List<CkTypeGraph> { ckType };
        var targetCkTypes = new List<CkTypeGraph> { ckType };

        // Act
        Dictionary<string, RtEntityTypeComparison> result = _comparator.Compare(
            sourceEntities, targetEntities, sourceCkTypes, targetCkTypes,
            [], [], _defaultOptions);

        // Assert
        Assert.Single(result);
        Assert.Single(result[ckTypeId].Differences);

        RtEntityDifference diff = result[ckTypeId].Differences[0];
        Assert.Single(diff.PropertyDifferences);
        Assert.Equal("Description", diff.PropertyDifferences[0].PropertyName);
        Assert.Equal(DifferenceType.Added, diff.PropertyDifferences[0].DifferenceType);
        Assert.Null(diff.PropertyDifferences[0].SourceValue);
        Assert.Equal("High-performance laptop", diff.PropertyDifferences[0].TargetValue);
    }

    [Fact]
    public void Compare_AttributeRemovedInTarget_DetectsRemoval()
    {
        // Arrange
        string ckTypeId = "Marketing/Marketing.Campaign";
        string rtId = "507f1f77bcf86cd799439011";

        var sourceEntity = CreateRtEntitiy(ckTypeId, rtId, null);
        sourceEntity.SetAttributeValue("Name", AttributeValueTypesDto.String, "Summer Sale 2025");
        sourceEntity.SetAttributeValue("Budget", AttributeValueTypesDto.Double, 50000.00);
        sourceEntity.SetAttributeValue("Label", AttributeValueTypesDto.String, "Q2 Campaign");

        var targetEntity = CreateRtEntitiy(ckTypeId, rtId, null);
        targetEntity.SetAttributeValue("Name", AttributeValueTypesDto.String, "Summer Sale 2025");
        targetEntity.SetAttributeValue("Budget", AttributeValueTypesDto.Double, 50000.00);
        // Label removed!

        var sourceEntities = new Dictionary<string, List<RtEntity>>
        {
            [ckTypeId] = [sourceEntity]
        };
        var targetEntities = new Dictionary<string, List<RtEntity>>
        {
            [ckTypeId] = [targetEntity]
        };

        var ckType = CreateCkType("Marketing", "Campaign", attributeNames: ["Name", "Budget", "Label"]);

        var sourceCkTypes = new List<CkTypeGraph> { ckType };
        var targetCkTypes = new List<CkTypeGraph> { ckType };

        // Act
        Dictionary<string, RtEntityTypeComparison> result = _comparator.Compare(
            sourceEntities, targetEntities, sourceCkTypes, targetCkTypes,
            [], [], _defaultOptions);

        // Assert
        Assert.Single(result);
        Assert.Single(result[ckTypeId].Differences);

        RtEntityDifference diff = result[ckTypeId].Differences[0];
        Assert.Single(diff.PropertyDifferences);
        Assert.Equal("Label", diff.PropertyDifferences[0].PropertyName);
        Assert.Equal(DifferenceType.Removed, diff.PropertyDifferences[0].DifferenceType);
        Assert.Equal("Q2 Campaign", diff.PropertyDifferences[0].SourceValue);
        Assert.Null(diff.PropertyDifferences[0].TargetValue);
    }

    [Fact]
    public void Compare_IncludePropertyDifferencesFalse_SkipsAttributeComparison()
    {
        // Arrange
        string ckTypeId = "Test/Test.Entity";
        string rtId = "507f1f77bcf86cd799439011";

        var sourceEntity = CreateRtEntitiy(ckTypeId, rtId, null);
        sourceEntity.SetAttributeValue("Name", AttributeValueTypesDto.String, "Value1");

        var targetEntity = CreateRtEntitiy(ckTypeId, rtId, null);
        targetEntity.SetAttributeValue("Name", AttributeValueTypesDto.String, "Value2"); // Different, but should be ignored

        var sourceEntities = new Dictionary<string, List<RtEntity>>
        {
            [ckTypeId] = [sourceEntity]
        };
        var targetEntities = new Dictionary<string, List<RtEntity>>
        {
            [ckTypeId] = [targetEntity]
        };

        var ckType = CreateCkType("Test", "Entity", attributeNames: ["Name"]);

        var sourceCkTypes = new List<CkTypeGraph> { ckType };
        var targetCkTypes = new List<CkTypeGraph> { ckType };

        var optionsWithoutProperties = new TenantComparisonOptions
        {
            IncludePropertyDifferences = false
        };

        // Act
        Dictionary<string, RtEntityTypeComparison> result = _comparator.Compare(
            sourceEntities, targetEntities, sourceCkTypes, targetCkTypes,
            [], [], optionsWithoutProperties);

        // Assert
        Assert.Single(result);
        Assert.Empty(result[ckTypeId].Differences);
        Assert.Equal(1, result[ckTypeId].MatchedIdenticalCount);
    }

    #endregion

    #region Multiple CkTypes Scenario

    [Fact]
    public void Compare_MultipleTypesWithComplexScenario_HandlesCorrectly()
    {
        // Arrange - Creating a complex e-commerce scenario with Customers, Orders, and Products

        // Customer type - some entities only in source, some matched
        string customerTypeId = "ECommerce/ECommerce.Customer";

        var sourceCust1 = CreateRtEntitiy(customerTypeId, "cust001", "AdminUser");
        sourceCust1.SetAttributeValue("Name", AttributeValueTypesDto.String, "Admin");
        sourceCust1.SetAttributeValue("Email", AttributeValueTypesDto.String, "admin@example.com");

        var sourceCust2 = CreateRtEntitiy(customerTypeId, "cust002", null);
        sourceCust2.SetAttributeValue("Name", AttributeValueTypesDto.String, "John Doe");
        sourceCust2.SetAttributeValue("Email", AttributeValueTypesDto.String, "john@example.com");

        var sourceCust3 = CreateRtEntitiy(customerTypeId, "cust003", null);
        sourceCust3.SetAttributeValue("Name", AttributeValueTypesDto.String, "Jane Smith");

        var sourceCustomers = new List<RtEntity> { sourceCust1, sourceCust2, sourceCust3 };

        var targetCust1 = CreateRtEntitiy(customerTypeId, "cust001", "AdminUser");
        targetCust1.SetAttributeValue("Name", AttributeValueTypesDto.String, "Admin");
        targetCust1.SetAttributeValue("Email", AttributeValueTypesDto.String, "admin@example.com");

        var targetCust2 = CreateRtEntitiy(customerTypeId, "cust002", null);
        targetCust2.SetAttributeValue("Name", AttributeValueTypesDto.String, "John Doe");
        targetCust2.SetAttributeValue("Email", AttributeValueTypesDto.String, "john.doe@example.com");

        var targetCust4 = CreateRtEntitiy(customerTypeId, "cust004", null);
        targetCust4.SetAttributeValue("Name", AttributeValueTypesDto.String, "Bob Johnson");

        var targetCustomers = new List<RtEntity> { targetCust1, targetCust2, targetCust4 };

        // Order type - all matched with differences
        string orderTypeId = "ECommerce/ECommerce.Order";

        var sourceOrder = CreateRtEntitiy(orderTypeId, "order001", "ORDER-001",
            createdDate: new DateTime(2025, 1, 15, 10, 0, 0, DateTimeKind.Utc),
            modifiedDate: new DateTime(2025, 1, 15, 10, 0, 0, DateTimeKind.Utc));
        sourceOrder.SetAttributeValue("OrderNumber", AttributeValueTypesDto.String, "ORDER-001");
        sourceOrder.SetAttributeValue("Status", AttributeValueTypesDto.String, "Pending");

        var targetOrder = CreateRtEntitiy(orderTypeId, "order001", "ORDER-001",
            createdDate: new DateTime(2025, 1, 15, 10, 0, 0, DateTimeKind.Utc),
            modifiedDate: new DateTime(2025, 1, 16, 14, 30, 0, DateTimeKind.Utc));
        targetOrder.SetAttributeValue("OrderNumber", AttributeValueTypesDto.String, "ORDER-001");
        targetOrder.SetAttributeValue("Status", AttributeValueTypesDto.String, "Shipped");

        var sourceOrders = new List<RtEntity> { sourceOrder };
        var targetOrders = new List<RtEntity> { targetOrder };

        // Product type - all matched, identical
        string productTypeId = "ECommerce/ECommerce.Product";

        var sourceProd1 = CreateRtEntitiy(productTypeId, "prod001", "SKU-LAPTOP-001");
        sourceProd1.SetAttributeValue("Name", AttributeValueTypesDto.String, "Laptop");
        sourceProd1.SetAttributeValue("Code", AttributeValueTypesDto.String, "SKU-LAPTOP-001");

        var sourceProd2 = CreateRtEntitiy(productTypeId, "prod002", "SKU-MOUSE-001");
        sourceProd2.SetAttributeValue("Name", AttributeValueTypesDto.String, "Mouse");
        sourceProd2.SetAttributeValue("Code", AttributeValueTypesDto.String, "SKU-MOUSE-001");

        var sourceProducts = new List<RtEntity> { sourceProd1, sourceProd2 };

        var targetProd1 = CreateRtEntitiy(productTypeId, "prod001", "SKU-LAPTOP-001");
        targetProd1.SetAttributeValue("Name", AttributeValueTypesDto.String, "Laptop");
        targetProd1.SetAttributeValue("Code", AttributeValueTypesDto.String, "SKU-LAPTOP-001");

        var targetProd2 = CreateRtEntitiy(productTypeId, "prod002", "SKU-MOUSE-001");
        targetProd2.SetAttributeValue("Name", AttributeValueTypesDto.String, "Mouse");
        targetProd2.SetAttributeValue("Code", AttributeValueTypesDto.String, "SKU-MOUSE-001");

        var targetProducts = new List<RtEntity> { targetProd1, targetProd2 };

        var sourceEntities = new Dictionary<string, List<RtEntity>>
        {
            [customerTypeId] = sourceCustomers,
            [orderTypeId] = sourceOrders,
            [productTypeId] = sourceProducts
        };
        var targetEntities = new Dictionary<string, List<RtEntity>>
        {
            [customerTypeId] = targetCustomers,
            [orderTypeId] = targetOrders,
            [productTypeId] = targetProducts
        };

        var customerType = CreateCkType("ECommerce", "Customer", attributeNames: ["Name", "Email"]);
        var orderType = CreateCkType("ECommerce", "Order", attributeNames: ["OrderNumber", "Status"]);
        var productType = CreateCkType("ECommerce", "Product", attributeNames: ["Name", "Code"]);

        var sourceCkTypes = new List<CkTypeGraph> { customerType, orderType, productType };
        var targetCkTypes = new List<CkTypeGraph> { customerType, orderType, productType };

        // Act
        Dictionary<string, RtEntityTypeComparison> result = _comparator.Compare(
            sourceEntities, targetEntities, sourceCkTypes, targetCkTypes,
            [], [], _defaultOptions);

        // Assert
        Assert.Equal(3, result.Count);

        // Verify Customer comparison
        var customerComparison = result[customerTypeId];
        Assert.Single(customerComparison.OnlyInSource); // cust003
        Assert.Single(customerComparison.OnlyInTarget); // cust004
        Assert.Single(customerComparison.Differences); // cust002 with email change
        Assert.Equal(1, customerComparison.MatchedIdenticalCount); // cust001
        Assert.Equal(3, customerComparison.TotalDifferences); // 1 + 1 + 1

        // Verify Order comparison
        var orderComparison = result[orderTypeId];
        Assert.Empty(orderComparison.OnlyInSource);
        Assert.Empty(orderComparison.OnlyInTarget);
        Assert.Single(orderComparison.Differences); // order001 with status and date changes
        Assert.Equal(0, orderComparison.MatchedIdenticalCount);
        Assert.Equal(1, orderComparison.TotalDifferences);

        var orderDiff = orderComparison.Differences[0];
        Assert.Equal(2, orderDiff.PropertyDifferences.Count); // Status and RtChangedDateTime

        // Verify Product comparison
        var productComparison = result[productTypeId];
        Assert.Empty(productComparison.OnlyInSource);
        Assert.Empty(productComparison.OnlyInTarget);
        Assert.Empty(productComparison.Differences);
        Assert.Equal(2, productComparison.MatchedIdenticalCount);
        Assert.Equal(0, productComparison.TotalDifferences);
    }

    #endregion

    #region Record Comparison Tests

    [Fact]
    public void Compare_EntitiesWithIdenticalRecordAttributes_MatchesIdentically()
    {
        // Arrange
        string ckTypeId = "OrderMgmt/OrderMgmt.Order";
        string rtId = "507f1f77bcf86cd799439011";
        string recordId = "Address";

        var addressRecord1 = CreateRtRecord("OrderMgmt", recordId, new Dictionary<string, object?>
        {
            ["Street"] = "123 Main St",
            ["City"] = "Boston",
            ["ZipCode"] = "02101"
        });

        var addressRecord2 = CreateRtRecord("OrderMgmt", recordId, new Dictionary<string, object?>
        {
            ["Street"] = "123 Main St",
            ["City"] = "Boston",
            ["ZipCode"] = "02101"
        });

        var sourceEntity = CreateRtEntitiy(ckTypeId, rtId, null);
        sourceEntity.SetAttributeValue("ShippingAddress", AttributeValueTypesDto.Record, addressRecord1);

        var targetEntity = CreateRtEntitiy(ckTypeId, rtId, null);
        targetEntity.SetAttributeValue("ShippingAddress", AttributeValueTypesDto.Record, addressRecord2);

        var sourceEntities = new Dictionary<string, List<RtEntity>> { [ckTypeId] = [sourceEntity] };
        var targetEntities = new Dictionary<string, List<RtEntity>> { [ckTypeId] = [targetEntity] };

        var ckType = CreateCkType("OrderMgmt", "Order", attributeNames: ["ShippingAddress"]);
        var sourceCkTypes = new List<CkTypeGraph> { ckType };
        var targetCkTypes = new List<CkTypeGraph> { ckType };

        var ckRecordGraph = CreateCkRecordGraph("OrderMgmt", recordId, "Street", "City", "ZipCode");
        var sourceCkRecords = new List<CkRecordGraph> { ckRecordGraph };
        var targetCkRecords = new List<CkRecordGraph> { ckRecordGraph };

        // Act
        Dictionary<string, RtEntityTypeComparison> result = _comparator.Compare(
            sourceEntities, targetEntities, sourceCkTypes, targetCkTypes,
            sourceCkRecords, targetCkRecords, _defaultOptions);

        // Assert
        Assert.Single(result);
        Assert.Empty(result[ckTypeId].Differences);
        Assert.Equal(1, result[ckTypeId].MatchedIdenticalCount);
    }

    [Fact]
    public void Compare_RecordsWithDifferentCkRecordIds_DetectsDifference()
    {
        // Arrange
        string ckTypeId = "Config/Config.Settings";
        string rtId = "507f1f77bcf86cd799439011";

        var record1 = CreateRtRecord("Config", "AddressRecord", new Dictionary<string, object?>
        {
            ["Value"] = "Test"
        });

        var record2 = CreateRtRecord("Config", "DifferentRecord", new Dictionary<string, object?>
        {
            ["Value"] = "Test"
        });

        var sourceEntity = CreateRtEntitiy(ckTypeId, rtId, null);
        sourceEntity.SetAttributeValue("ConfigValue", AttributeValueTypesDto.Record, record1);

        var targetEntity = CreateRtEntitiy(ckTypeId, rtId, null);
        targetEntity.SetAttributeValue("ConfigValue", AttributeValueTypesDto.Record, record2);

        var sourceEntities = new Dictionary<string, List<RtEntity>> { [ckTypeId] = [sourceEntity] };
        var targetEntities = new Dictionary<string, List<RtEntity>> { [ckTypeId] = [targetEntity] };

        var ckType = CreateCkType("Config", "Settings", attributeNames: ["ConfigValue"]);
        var sourceCkTypes = new List<CkTypeGraph> { ckType };
        var targetCkTypes = new List<CkTypeGraph> { ckType };

        var ckRecordGraph1 = CreateCkRecordGraph("Config", "AddressRecord", "Value");
        var ckRecordGraph2 = CreateCkRecordGraph("Config", "DifferentRecord", "Value");
        var sourceCkRecords = new List<CkRecordGraph> { ckRecordGraph1 };
        var targetCkRecords = new List<CkRecordGraph> { ckRecordGraph2 };

        // Act
        Dictionary<string, RtEntityTypeComparison> result = _comparator.Compare(
            sourceEntities, targetEntities, sourceCkTypes, targetCkTypes,
            sourceCkRecords, targetCkRecords, _defaultOptions);

        // Assert
        Assert.Single(result);
        Assert.Single(result[ckTypeId].Differences);
        Assert.Equal("ConfigValue", result[ckTypeId].Differences[0].PropertyDifferences[0].PropertyName);
    }

    [Fact]
    public void Compare_RecordsWithDifferentAttributeValues_DetectsDifference()
    {
        // Arrange
        string ckTypeId = "Customer/Customer.Person";
        string rtId = "507f1f77bcf86cd799439011";
        string recordId = "ContactInfo";

        var contactRecord1 = CreateRtRecord("Customer", recordId, new Dictionary<string, object?>
        {
            ["Email"] = "old@example.com",
            ["Phone"] = "555-1234"
        });

        var contactRecord2 = CreateRtRecord("Customer", recordId, new Dictionary<string, object?>
        {
            ["Email"] = "new@example.com",
            ["Phone"] = "555-1234"
        });

        var sourceEntity = CreateRtEntitiy(ckTypeId, rtId, null);
        sourceEntity.SetAttributeValue("Contact", AttributeValueTypesDto.Record, contactRecord1);

        var targetEntity = CreateRtEntitiy(ckTypeId, rtId, null);
        targetEntity.SetAttributeValue("Contact", AttributeValueTypesDto.Record, contactRecord2);

        var sourceEntities = new Dictionary<string, List<RtEntity>> { [ckTypeId] = [sourceEntity] };
        var targetEntities = new Dictionary<string, List<RtEntity>> { [ckTypeId] = [targetEntity] };

        var ckType = CreateCkType("Customer", "Person", attributeNames: ["Contact"]);
        var sourceCkTypes = new List<CkTypeGraph> { ckType };
        var targetCkTypes = new List<CkTypeGraph> { ckType };

        var ckRecordGraph = CreateCkRecordGraph("Customer", recordId, "Email", "Phone");
        var sourceCkRecords = new List<CkRecordGraph> { ckRecordGraph };
        var targetCkRecords = new List<CkRecordGraph> { ckRecordGraph };

        // Act
        Dictionary<string, RtEntityTypeComparison> result = _comparator.Compare(
            sourceEntities, targetEntities, sourceCkTypes, targetCkTypes,
            sourceCkRecords, targetCkRecords, _defaultOptions);

        // Assert
        Assert.Single(result);
        Assert.Single(result[ckTypeId].Differences);
        Assert.Equal("Contact", result[ckTypeId].Differences[0].PropertyDifferences[0].PropertyName);
    }

    [Fact]
    public void Compare_RecordsWithoutMetadata_FallsBackToEquals()
    {
        // Arrange
        string ckTypeId = "Test/Test.Entity";
        string rtId = "507f1f77bcf86cd799439011";

        var record1 = CreateRtRecord("Test", "UnknownRecord", new Dictionary<string, object?>
        {
            ["Value"] = "Same"
        });

        var record2 = CreateRtRecord("Test", "UnknownRecord", new Dictionary<string, object?>
        {
            ["Value"] = "Same"
        });

        var sourceEntity = CreateRtEntitiy(ckTypeId, rtId, null);
        sourceEntity.SetAttributeValue("Data", AttributeValueTypesDto.Record, record1);

        var targetEntity = CreateRtEntitiy(ckTypeId, rtId, null);
        targetEntity.SetAttributeValue("Data", AttributeValueTypesDto.Record, record2);

        var sourceEntities = new Dictionary<string, List<RtEntity>> { [ckTypeId] = [sourceEntity] };
        var targetEntities = new Dictionary<string, List<RtEntity>> { [ckTypeId] = [targetEntity] };

        var ckType = CreateCkType("Test", "Entity", attributeNames: ["Data"]);
        var sourceCkTypes = new List<CkTypeGraph> { ckType };
        var targetCkTypes = new List<CkTypeGraph> { ckType };

        // No CkRecordGraph provided - should fall back to Equals
        var sourceCkRecords = new List<CkRecordGraph>();
        var targetCkRecords = new List<CkRecordGraph>();

        // Act
        Dictionary<string, RtEntityTypeComparison> result = _comparator.Compare(
            sourceEntities, targetEntities, sourceCkTypes, targetCkTypes,
            sourceCkRecords, targetCkRecords, _defaultOptions);

        // Assert - Will use RtRecord.Equals which may detect differences or not depending on implementation
        Assert.Single(result);
    }

    [Fact]
    public void Compare_RecordsWithAddedOrRemovedAttributes_DetectsDifference()
    {
        // Arrange
        string ckTypeId = "Product/Product.Item";
        string rtId = "507f1f77bcf86cd799439011";
        string recordId = "Dimensions";

        var dimensionsRecord1 = CreateRtRecord("Product", recordId, new Dictionary<string, object?>
        {
            ["Width"] = 10.5,
            ["Height"] = 20.0,
            ["Depth"] = 5.0
        });

        var dimensionsRecord2 = CreateRtRecord("Product", recordId, new Dictionary<string, object?>
        {
            ["Width"] = 10.5,
            ["Height"] = 20.0
            // Depth removed!
        });

        var sourceEntity = CreateRtEntitiy(ckTypeId, rtId, null);
        sourceEntity.SetAttributeValue("Dimensions", AttributeValueTypesDto.Record, dimensionsRecord1);

        var targetEntity = CreateRtEntitiy(ckTypeId, rtId, null);
        targetEntity.SetAttributeValue("Dimensions", AttributeValueTypesDto.Record, dimensionsRecord2);

        var sourceEntities = new Dictionary<string, List<RtEntity>> { [ckTypeId] = [sourceEntity] };
        var targetEntities = new Dictionary<string, List<RtEntity>> { [ckTypeId] = [targetEntity] };

        var ckType = CreateCkType("Product", "Item", attributeNames: ["Dimensions"]);
        var sourceCkTypes = new List<CkTypeGraph> { ckType };
        var targetCkTypes = new List<CkTypeGraph> { ckType };

        var ckRecordGraph = CreateCkRecordGraph("Product", recordId, "Width", "Height", "Depth");
        var sourceCkRecords = new List<CkRecordGraph> { ckRecordGraph };
        var targetCkRecords = new List<CkRecordGraph> { ckRecordGraph };

        // Act
        Dictionary<string, RtEntityTypeComparison> result = _comparator.Compare(
            sourceEntities, targetEntities, sourceCkTypes, targetCkTypes,
            sourceCkRecords, targetCkRecords, _defaultOptions);

        // Assert
        Assert.Single(result);
        Assert.Single(result[ckTypeId].Differences);
        Assert.Equal("Dimensions", result[ckTypeId].Differences[0].PropertyDifferences[0].PropertyName);
    }

    #endregion

    #region Record List Comparison Tests

    [Fact]
    public void Compare_EntitiesWithIdenticalRecordLists_MatchesIdentically()
    {
        // Arrange
        string ckTypeId = "Order/Order.Purchase";
        string rtId = "507f1f77bcf86cd799439011";
        string recordId = "LineItem";

        var lineItems1 = new List<RtRecord>
        {
            CreateRtRecord("Order", recordId, new Dictionary<string, object?>
            {
                ["ProductCode"] = "ITEM-001",
                ["Quantity"] = 5,
                ["Price"] = 29.99
            }),
            CreateRtRecord("Order", recordId, new Dictionary<string, object?>
            {
                ["ProductCode"] = "ITEM-002",
                ["Quantity"] = 2,
                ["Price"] = 49.99
            })
        };

        var lineItems2 = new List<RtRecord>
        {
            CreateRtRecord("Order", recordId, new Dictionary<string, object?>
            {
                ["ProductCode"] = "ITEM-001",
                ["Quantity"] = 5,
                ["Price"] = 29.99
            }),
            CreateRtRecord("Order", recordId, new Dictionary<string, object?>
            {
                ["ProductCode"] = "ITEM-002",
                ["Quantity"] = 2,
                ["Price"] = 49.99
            })
        };

        var sourceEntity = CreateRtEntitiy(ckTypeId, rtId, null);
        sourceEntity.SetAttributeValue("LineItems", AttributeValueTypesDto.RecordArray, lineItems1);

        var targetEntity = CreateRtEntitiy(ckTypeId, rtId, null);
        targetEntity.SetAttributeValue("LineItems", AttributeValueTypesDto.RecordArray, lineItems2);

        var sourceEntities = new Dictionary<string, List<RtEntity>> { [ckTypeId] = [sourceEntity] };
        var targetEntities = new Dictionary<string, List<RtEntity>> { [ckTypeId] = [targetEntity] };

        var ckType = CreateCkType("Order", "Purchase", attributeNames: ["LineItems"]);
        var sourceCkTypes = new List<CkTypeGraph> { ckType };
        var targetCkTypes = new List<CkTypeGraph> { ckType };

        var ckRecordGraph = CreateCkRecordGraph("Order", recordId, "ProductCode", "Quantity", "Price");
        var sourceCkRecords = new List<CkRecordGraph> { ckRecordGraph };
        var targetCkRecords = new List<CkRecordGraph> { ckRecordGraph };

        // Act
        Dictionary<string, RtEntityTypeComparison> result = _comparator.Compare(
            sourceEntities, targetEntities, sourceCkTypes, targetCkTypes,
            sourceCkRecords, targetCkRecords, _defaultOptions);

        // Assert
        Assert.Single(result);
        Assert.Empty(result[ckTypeId].Differences);
        Assert.Equal(1, result[ckTypeId].MatchedIdenticalCount);
    }

    [Fact]
    public void Compare_RecordListsWithDifferentCounts_DetectsDifference()
    {
        // Arrange
        string ckTypeId = "Invoice/Invoice.Document";
        string rtId = "507f1f77bcf86cd799439011";
        string recordId = "TaxItem";

        var taxItems1 = new List<RtRecord>
        {
            CreateRtRecord("Invoice", recordId, new Dictionary<string, object?>
            {
                ["TaxType"] = "VAT",
                ["Rate"] = 20.0
            }),
            CreateRtRecord("Invoice", recordId, new Dictionary<string, object?>
            {
                ["TaxType"] = "Sales Tax",
                ["Rate"] = 8.5
            })
        };

        var taxItems2 = new List<RtRecord>
        {
            CreateRtRecord("Invoice", recordId, new Dictionary<string, object?>
            {
                ["TaxType"] = "VAT",
                ["Rate"] = 20.0
            })
            // One item missing
        };

        var sourceEntity = CreateRtEntitiy(ckTypeId, rtId, null);
        sourceEntity.SetAttributeValue("Taxes", AttributeValueTypesDto.RecordArray, taxItems1);

        var targetEntity = CreateRtEntitiy(ckTypeId, rtId, null);
        targetEntity.SetAttributeValue("Taxes", AttributeValueTypesDto.RecordArray, taxItems2);

        var sourceEntities = new Dictionary<string, List<RtEntity>> { [ckTypeId] = [sourceEntity] };
        var targetEntities = new Dictionary<string, List<RtEntity>> { [ckTypeId] = [targetEntity] };

        var ckType = CreateCkType("Invoice", "Document", attributeNames: ["Taxes"]);
        var sourceCkTypes = new List<CkTypeGraph> { ckType };
        var targetCkTypes = new List<CkTypeGraph> { ckType };

        var ckRecordGraph = CreateCkRecordGraph("Invoice", recordId, "TaxType", "Rate");
        var sourceCkRecords = new List<CkRecordGraph> { ckRecordGraph };
        var targetCkRecords = new List<CkRecordGraph> { ckRecordGraph };

        // Act
        Dictionary<string, RtEntityTypeComparison> result = _comparator.Compare(
            sourceEntities, targetEntities, sourceCkTypes, targetCkTypes,
            sourceCkRecords, targetCkRecords, _defaultOptions);

        // Assert
        Assert.Single(result);
        Assert.Single(result[ckTypeId].Differences);
        Assert.Equal("Taxes", result[ckTypeId].Differences[0].PropertyDifferences[0].PropertyName);
    }

    [Fact]
    public void Compare_RecordListsWithDifferentValues_DetectsDifference()
    {
        // Arrange
        string ckTypeId = "Booking/Booking.Reservation";
        string rtId = "507f1f77bcf86cd799439011";
        string recordId = "Guest";

        var guests1 = new List<RtRecord>
        {
            CreateRtRecord("Booking", recordId, new Dictionary<string, object?>
            {
                ["Name"] = "John Doe",
                ["Age"] = 35
            }),
            CreateRtRecord("Booking", recordId, new Dictionary<string, object?>
            {
                ["Name"] = "Jane Smith",
                ["Age"] = 28
            })
        };

        var guests2 = new List<RtRecord>
        {
            CreateRtRecord("Booking", recordId, new Dictionary<string, object?>
            {
                ["Name"] = "John Doe",
                ["Age"] = 35
            }),
            CreateRtRecord("Booking", recordId, new Dictionary<string, object?>
            {
                ["Name"] = "Jane Smith",
                ["Age"] = 29 // Different age
            })
        };

        var sourceEntity = CreateRtEntitiy(ckTypeId, rtId, null);
        sourceEntity.SetAttributeValue("Guests", AttributeValueTypesDto.RecordArray, guests1);

        var targetEntity = CreateRtEntitiy(ckTypeId, rtId, null);
        targetEntity.SetAttributeValue("Guests", AttributeValueTypesDto.RecordArray, guests2);

        var sourceEntities = new Dictionary<string, List<RtEntity>> { [ckTypeId] = [sourceEntity] };
        var targetEntities = new Dictionary<string, List<RtEntity>> { [ckTypeId] = [targetEntity] };

        var ckType = CreateCkType("Booking", "Reservation", attributeNames: ["Guests"]);
        var sourceCkTypes = new List<CkTypeGraph> { ckType };
        var targetCkTypes = new List<CkTypeGraph> { ckType };

        var ckRecordGraph = CreateCkRecordGraph("Booking", recordId, "Name", "Age");
        var sourceCkRecords = new List<CkRecordGraph> { ckRecordGraph };
        var targetCkRecords = new List<CkRecordGraph> { ckRecordGraph };

        // Act
        Dictionary<string, RtEntityTypeComparison> result = _comparator.Compare(
            sourceEntities, targetEntities, sourceCkTypes, targetCkTypes,
            sourceCkRecords, targetCkRecords, _defaultOptions);

        // Assert
        Assert.Single(result);
        Assert.Single(result[ckTypeId].Differences);
        Assert.Equal("Guests", result[ckTypeId].Differences[0].PropertyDifferences[0].PropertyName);
    }

    #endregion

    #region Collection Comparison Tests

    [Fact]
    public void Compare_EntitiesWithIdenticalPrimitiveCollections_MatchesIdentically()
    {
        // Arrange
        string ckTypeId = "Content/Content.Article";
        string rtId = "507f1f77bcf86cd799439011";

        var tags1 = new List<string> { "technology", "programming", "dotnet" };
        var tags2 = new List<string> { "technology", "programming", "dotnet" };

        var sourceEntity = CreateRtEntitiy(ckTypeId, rtId, null);
        sourceEntity.SetAttributeValue("Tags", AttributeValueTypesDto.StringArray, tags1);

        var targetEntity = CreateRtEntitiy(ckTypeId, rtId, null);
        targetEntity.SetAttributeValue("Tags", AttributeValueTypesDto.StringArray, tags2);

        var sourceEntities = new Dictionary<string, List<RtEntity>> { [ckTypeId] = [sourceEntity] };
        var targetEntities = new Dictionary<string, List<RtEntity>> { [ckTypeId] = [targetEntity] };

        var ckType = CreateCkType("Content", "Article", attributeNames: ["Tags"]);
        var sourceCkTypes = new List<CkTypeGraph> { ckType };
        var targetCkTypes = new List<CkTypeGraph> { ckType };

        // Act
        Dictionary<string, RtEntityTypeComparison> result = _comparator.Compare(
            sourceEntities, targetEntities, sourceCkTypes, targetCkTypes,
            [], [], _defaultOptions);

        // Assert
        Assert.Single(result);
        Assert.Empty(result[ckTypeId].Differences);
        Assert.Equal(1, result[ckTypeId].MatchedIdenticalCount);
    }

    [Fact]
    public void Compare_CollectionsWithDifferentCounts_DetectsDifference()
    {
        // Arrange
        string ckTypeId = "Analytics/Analytics.Report";
        string rtId = "507f1f77bcf86cd799439011";

        var metrics1 = new List<int> { 100, 250, 500, 750 };
        var metrics2 = new List<int> { 100, 250, 500 }; // One fewer element

        var sourceEntity = CreateRtEntitiy(ckTypeId, rtId, null);
        sourceEntity.SetAttributeValue("Metrics", AttributeValueTypesDto.IntArray, metrics1);

        var targetEntity = CreateRtEntitiy(ckTypeId, rtId, null);
        targetEntity.SetAttributeValue("Metrics", AttributeValueTypesDto.IntArray, metrics2);

        var sourceEntities = new Dictionary<string, List<RtEntity>> { [ckTypeId] = [sourceEntity] };
        var targetEntities = new Dictionary<string, List<RtEntity>> { [ckTypeId] = [targetEntity] };

        var ckType = CreateCkType("Analytics", "Report", attributeNames: ["Metrics"]);
        var sourceCkTypes = new List<CkTypeGraph> { ckType };
        var targetCkTypes = new List<CkTypeGraph> { ckType };

        // Act
        Dictionary<string, RtEntityTypeComparison> result = _comparator.Compare(
            sourceEntities, targetEntities, sourceCkTypes, targetCkTypes,
            [], [], _defaultOptions);

        // Assert
        Assert.Single(result);
        Assert.Single(result[ckTypeId].Differences);
        Assert.Equal("Metrics", result[ckTypeId].Differences[0].PropertyDifferences[0].PropertyName);
    }

    [Fact]
    public void Compare_CollectionsWithDifferentValues_DetectsDifference()
    {
        // Arrange
        string ckTypeId = "Survey/Survey.Response";
        string rtId = "507f1f77bcf86cd799439011";

        var scores1 = new List<int> { 4, 3, 5, 4 };
        var scores2 = new List<int> { 4, 3, 4, 4 }; // Third element different

        var sourceEntity = CreateRtEntitiy(ckTypeId, rtId, null);
        sourceEntity.SetAttributeValue("Scores", AttributeValueTypesDto.IntArray, scores1);

        var targetEntity = CreateRtEntitiy(ckTypeId, rtId, null);
        targetEntity.SetAttributeValue("Scores", AttributeValueTypesDto.IntArray, scores2);

        var sourceEntities = new Dictionary<string, List<RtEntity>> { [ckTypeId] = [sourceEntity] };
        var targetEntities = new Dictionary<string, List<RtEntity>> { [ckTypeId] = [targetEntity] };

        var ckType = CreateCkType("Survey", "Response", attributeNames: ["Scores"]);
        var sourceCkTypes = new List<CkTypeGraph> { ckType };
        var targetCkTypes = new List<CkTypeGraph> { ckType };

        // Act
        Dictionary<string, RtEntityTypeComparison> result = _comparator.Compare(
            sourceEntities, targetEntities, sourceCkTypes, targetCkTypes,
            [], [], _defaultOptions);

        // Assert
        Assert.Single(result);
        Assert.Single(result[ckTypeId].Differences);
        Assert.Equal("Scores", result[ckTypeId].Differences[0].PropertyDifferences[0].PropertyName);
    }

    #endregion

    #region Nested/Complex Scenarios

    [Fact]
    public void Compare_RecordsContainingNestedRecords_ComparesRecursively()
    {
        // Arrange
        string ckTypeId = "HR/HR.Employee";
        string rtId = "507f1f77bcf86cd799439011";

        // Create nested address record
        var address1 = CreateRtRecord("HR", "Address", new Dictionary<string, object?>
        {
            ["Street"] = "123 Main St",
            ["City"] = "Boston"
        });

        var address2 = CreateRtRecord("HR", "Address", new Dictionary<string, object?>
        {
            ["Street"] = "456 Oak Ave", // Different street
            ["City"] = "Boston"
        });

        // Create contact record that contains nested address
        var contact1 = CreateRtRecord("HR", "Contact", new Dictionary<string, object?>
        {
            ["Email"] = "john@example.com",
            ["HomeAddress"] = address1
        });

        var contact2 = CreateRtRecord("HR", "Contact", new Dictionary<string, object?>
        {
            ["Email"] = "john@example.com",
            ["HomeAddress"] = address2
        });

        var sourceEntity = CreateRtEntitiy(ckTypeId, rtId, null);
        sourceEntity.SetAttributeValue("ContactInfo", AttributeValueTypesDto.Record, contact1);

        var targetEntity = CreateRtEntitiy(ckTypeId, rtId, null);
        targetEntity.SetAttributeValue("ContactInfo", AttributeValueTypesDto.Record, contact2);

        var sourceEntities = new Dictionary<string, List<RtEntity>> { [ckTypeId] = [sourceEntity] };
        var targetEntities = new Dictionary<string, List<RtEntity>> { [ckTypeId] = [targetEntity] };

        var ckType = CreateCkType("HR", "Employee", attributeNames: ["ContactInfo"]);
        var sourceCkTypes = new List<CkTypeGraph> { ckType };
        var targetCkTypes = new List<CkTypeGraph> { ckType };

        var addressRecordGraph = CreateCkRecordGraph("HR", "Address", "Street", "City");
        var contactRecordGraph = CreateCkRecordGraph("HR", "Contact", "Email", "HomeAddress");
        var sourceCkRecords = new List<CkRecordGraph> { addressRecordGraph, contactRecordGraph };
        var targetCkRecords = new List<CkRecordGraph> { addressRecordGraph, contactRecordGraph };

        // Act
        Dictionary<string, RtEntityTypeComparison> result = _comparator.Compare(
            sourceEntities, targetEntities, sourceCkTypes, targetCkTypes,
            sourceCkRecords, targetCkRecords, _defaultOptions);

        // Assert
        Assert.Single(result);
        Assert.Single(result[ckTypeId].Differences);
        Assert.Equal("ContactInfo", result[ckTypeId].Differences[0].PropertyDifferences[0].PropertyName);
    }

    [Fact]
    public void Compare_ComplexEntityWithMixedAttributeTypes_DetectsAllDifferences()
    {
        // Arrange - Complex entity with primitives, records, and collections
        string ckTypeId = "Shop/Shop.Order";
        string rtId = "507f1f77bcf86cd799439011";

        // Create shipping address records
        var shippingAddress1 = CreateRtRecord("Shop", "Address", new Dictionary<string, object?>
        {
            ["Street"] = "100 Commerce Blvd",
            ["City"] = "Seattle"
        });

        var shippingAddress2 = CreateRtRecord("Shop", "Address", new Dictionary<string, object?>
        {
            ["Street"] = "100 Commerce Blvd",
            ["City"] = "Seattle"
        });

        // Create line item records
        var lineItems1 = new List<RtRecord>
        {
            CreateRtRecord("Shop", "LineItem", new Dictionary<string, object?>
            {
                ["ProductName"] = "Widget",
                ["Quantity"] = 2
            })
        };

        var lineItems2 = new List<RtRecord>
        {
            CreateRtRecord("Shop", "LineItem", new Dictionary<string, object?>
            {
                ["ProductName"] = "Widget",
                ["Quantity"] = 3 // Different quantity
            })
        };

        var sourceEntity = CreateRtEntitiy(ckTypeId, rtId, null);
        sourceEntity.SetAttributeValue("OrderNumber", AttributeValueTypesDto.String, "ORD-12345");
        sourceEntity.SetAttributeValue("TotalAmount", AttributeValueTypesDto.Double, 299.99);
        sourceEntity.SetAttributeValue("ShippingAddress", AttributeValueTypesDto.Record, shippingAddress1);
        sourceEntity.SetAttributeValue("Items", AttributeValueTypesDto.RecordArray, lineItems1);
        sourceEntity.SetAttributeValue("Tags", AttributeValueTypesDto.StringArray, new List<string> { "priority", "express" });

        var targetEntity = CreateRtEntitiy(ckTypeId, rtId, null);
        targetEntity.SetAttributeValue("OrderNumber", AttributeValueTypesDto.String, "ORD-12345");
        targetEntity.SetAttributeValue("TotalAmount", AttributeValueTypesDto.Double, 399.99); // Different amount
        targetEntity.SetAttributeValue("ShippingAddress", AttributeValueTypesDto.Record, shippingAddress2);
        targetEntity.SetAttributeValue("Items", AttributeValueTypesDto.RecordArray, lineItems2); // Different items
        targetEntity.SetAttributeValue("Tags", AttributeValueTypesDto.StringArray, new List<string> { "priority", "express" });

        var sourceEntities = new Dictionary<string, List<RtEntity>> { [ckTypeId] = [sourceEntity] };
        var targetEntities = new Dictionary<string, List<RtEntity>> { [ckTypeId] = [targetEntity] };

        var ckType = CreateCkType("Shop", "Order", attributeNames: ["OrderNumber", "TotalAmount", "ShippingAddress", "Items", "Tags",
        ]);
        var sourceCkTypes = new List<CkTypeGraph> { ckType };
        var targetCkTypes = new List<CkTypeGraph> { ckType };

        var addressRecordGraph = CreateCkRecordGraph("Shop", "Address", "Street", "City");
        var lineItemRecordGraph = CreateCkRecordGraph("Shop", "LineItem", "ProductName", "Quantity");
        var sourceCkRecords = new List<CkRecordGraph> { addressRecordGraph, lineItemRecordGraph };
        var targetCkRecords = new List<CkRecordGraph> { addressRecordGraph, lineItemRecordGraph };

        // Act
        Dictionary<string, RtEntityTypeComparison> result = _comparator.Compare(
            sourceEntities, targetEntities, sourceCkTypes, targetCkTypes,
            sourceCkRecords, targetCkRecords, _defaultOptions);

        // Assert
        Assert.Single(result);
        Assert.Single(result[ckTypeId].Differences);

        RtEntityDifference diff = result[ckTypeId].Differences[0];
        Assert.Equal(2, diff.PropertyDifferences.Count); // TotalAmount and Items

        Assert.Contains(diff.PropertyDifferences, pd => pd.PropertyName == "TotalAmount");
        Assert.Contains(diff.PropertyDifferences, pd => pd.PropertyName == "Items");
    }

    #endregion

    #region Edge Cases

    [Fact]
    public void Compare_EmptyListsForSpecificCkType_ReturnsZeroCounts()
    {
        // Arrange
        string ckTypeId = "Test/Test.EmptyType";
        var sourceEntities = new Dictionary<string, List<RtEntity>>
        {
            [ckTypeId] = []
        };
        var targetEntities = new Dictionary<string, List<RtEntity>>
        {
            [ckTypeId] = []
        };
        var sourceCkTypes = new List<CkTypeGraph> { CreateCkType("Test", "EmptyType") };
        var targetCkTypes = new List<CkTypeGraph> { CreateCkType("Test", "EmptyType") };

        // Act
        Dictionary<string, RtEntityTypeComparison> result = _comparator.Compare(
            sourceEntities, targetEntities, sourceCkTypes, targetCkTypes,
            [], [], _defaultOptions);

        // Assert
        Assert.Single(result);
        Assert.True(result.ContainsKey(ckTypeId));
        Assert.Empty(result[ckTypeId].OnlyInSource);
        Assert.Empty(result[ckTypeId].OnlyInTarget);
        Assert.Empty(result[ckTypeId].Differences);
        Assert.Equal(0, result[ckTypeId].MatchedIdenticalCount);
        Assert.Equal(0, result[ckTypeId].TotalDifferences);
    }

    [Fact]
    public void Compare_NullWellKnownNames_HandlesGracefully()
    {
        // Arrange
        string ckTypeId = "Test/Test.Entity";
        var sourceEntity = CreateRtEntitiy(ckTypeId, "entity001", null);
        var targetEntity = CreateRtEntitiy(ckTypeId, "entity001", null);

        var sourceEntities = new Dictionary<string, List<RtEntity>>
        {
            [ckTypeId] = [sourceEntity]
        };
        var targetEntities = new Dictionary<string, List<RtEntity>>
        {
            [ckTypeId] = [targetEntity]
        };
        var sourceCkTypes = new List<CkTypeGraph> { CreateCkType("Test", "Entity") };
        var targetCkTypes = new List<CkTypeGraph> { CreateCkType("Test", "Entity") };

        // Act
        Dictionary<string, RtEntityTypeComparison> result = _comparator.Compare(
            sourceEntities, targetEntities, sourceCkTypes, targetCkTypes,
            [], [], _defaultOptions);

        // Assert - Should match by RtId without issues
        Assert.Single(result);
        Assert.Equal(1, result[ckTypeId].MatchedIdenticalCount);
    }

    [Fact]
    public void Compare_MultipleEntitiesWithSameWellKnownName_MatchesFirstOne()
    {
        // Arrange
        string ckTypeId = "Test/Test.Config";
        string wellKnownName = "DefaultConfig";

        var sourceEntities = new Dictionary<string, List<RtEntity>>
        {
            [ckTypeId] =
            [
                CreateRtEntitiy(ckTypeId, "config001", wellKnownName),
                CreateRtEntitiy(ckTypeId, "config002", wellKnownName),
            ]
        };
        var targetEntities = new Dictionary<string, List<RtEntity>>
        {
            [ckTypeId] = [CreateRtEntitiy(ckTypeId, "config999", wellKnownName)]
        };
        var sourceCkTypes = new List<CkTypeGraph> { CreateCkType("Test", "Config") };
        var targetCkTypes = new List<CkTypeGraph> { CreateCkType("Test", "Config") };

        // Act
        Dictionary<string, RtEntityTypeComparison> result = _comparator.Compare(
            sourceEntities, targetEntities, sourceCkTypes, targetCkTypes,
            [], [], _defaultOptions);

        // Assert - Should match one and leave one unmatched
        Assert.Single(result);
        Assert.Single(result[ckTypeId].OnlyInSource); // config002 couldn't match
        Assert.Equal(1, result[ckTypeId].MatchedIdenticalCount); // config001 matched to config999
    }

    #endregion

    #region Helper Methods

    private RtEntity CreateRtEntitiy(
        string ckTypeId,
        string rtId,
        string? wellKnownName = null,
        DateTime? createdDate = null,
        DateTime? modifiedDate = null)
    {
        return new RtEntity
        {
            RtId = new OctoObjectId(ConvertToValidObjectId(rtId)),
            CkTypeId = ParseCkTypeId(ckTypeId),
            RtWellKnownName = wellKnownName,
            RtCreationDateTime = createdDate,
            RtChangedDateTime = modifiedDate
        };
    }

    private string ConvertToValidObjectId(string input)
    {
        // If already a valid 24-char hex string, return as-is
        if (input.Length == 24 && input.All(c => "0123456789abcdefABCDEF".Contains(c)))
        {
            return input;
        }

        // Convert input string to a deterministic 24-character hex string
        // Use SHA256 hash of the input and take first 24 chars
        using System.Security.Cryptography.SHA256 sha256 = System.Security.Cryptography.SHA256.Create();
        byte[] hashBytes = sha256.ComputeHash(System.Text.Encoding.UTF8.GetBytes(input));
        string hex = BitConverter.ToString(hashBytes).Replace("-", "").ToLower();
        return hex.Substring(0, 24);
    }

    private CkId<CkTypeId> ParseCkTypeId(string ckTypeIdString)
    {
        string[] parts = ckTypeIdString.Split('/');
        return new CkId<CkTypeId>(parts[0], new CkTypeId(parts[1]));
    }

    private CkTypeGraph CreateCkType(
        string modelId,
        string typeId,
        bool isFinal = false,
        bool isAbstract = false,
        string? description = null,
        bool isCollectionRoot = true,
        string? collectionName = null,
        params string[] attributeNames)
    {
        CkId<CkTypeId> ckTypeId = new CkId<CkTypeId>(modelId, new CkTypeId($"{modelId}.{typeId}"));

        // Create AllAttributesByName dictionary
        var allAttributesByName = new Dictionary<string, CkTypeAttributeGraph>();
        var allAttributes = new Dictionary<CkId<CkAttributeId>, CkTypeAttributeGraph>();

        foreach (string attrName in attributeNames)
        {
            CkId<CkAttributeId> attrId = new CkId<CkAttributeId>(modelId, new CkAttributeId($"{modelId}.{attrName}"));
            var attrGraph = new CkTypeAttributeGraph(
                ckAttributeId: attrId,
                attributeName: attrName,
                autoCompleteValues: null,
                valueType: AttributeValueTypesDto.String,
                valueCkRecordId: null,
                valueCkEnumId: null,
                autoIncrementReference: null,
                metaData: null,
                isDataStream: false,
                defaultValues: null,
                isOptional: false,
                description: null);

            allAttributes.Add(attrId, attrGraph);
            allAttributesByName.Add(attrName, attrGraph);
        }

        return new CkTypeGraph(
            ckTypeId: ckTypeId,
            isAbstract: isAbstract,
            isFinal: isFinal,
            isCollectionRoot: isCollectionRoot,
            isStreamType: false,
            baseTypes: [],
            derivedFromCkTypeId: null,
            definingCollectionRootCkTypeId: null,
            derivedTypes: [],
            definedAttributes: [],
            allAttributes: allAttributes,
            indexes: [],
            associations: new CkGraphDirectedAssociations(Array.Empty<CkTypeAssociationDto>()),
            description: description ?? string.Empty);
    }

    private CkRecordGraph CreateCkRecordGraph(
        string modelId,
        string recordId,
        params string[] attributeNames)
    {
        CkId<CkRecordId> ckRecordId = new CkId<CkRecordId>(modelId, new CkRecordId($"{modelId}.{recordId}"));

        // Create attributes dictionary
        var allAttributes = new Dictionary<CkId<CkAttributeId>, CkTypeAttributeGraph>();

        foreach (string attrName in attributeNames)
        {
            CkId<CkAttributeId> attrId = new CkId<CkAttributeId>(modelId, new CkAttributeId($"{modelId}.{attrName}"));
            var attrGraph = new CkTypeAttributeGraph(
                ckAttributeId: attrId,
                attributeName: attrName,
                autoCompleteValues: null,
                valueType: AttributeValueTypesDto.String,
                valueCkRecordId: null,
                valueCkEnumId: null,
                autoIncrementReference: null,
                metaData: null,
                isDataStream: false,
                defaultValues: null,
                isOptional: false,
                description: null);

            allAttributes.Add(attrId, attrGraph);
        }

        return new CkRecordGraph(
            ckRecordId: ckRecordId,
            isAbstract: false,
            isFinal: false,
            baseRecords: [],
            derivedFromCkRecordId: null,
            derivedRecords: [],
            definedAttributes: [],
            allAttributes: allAttributes,
            description: string.Empty);
    }

    private RtRecord CreateRtRecord(
        string modelId,
        string recordId,
        Dictionary<string, object?>? attributes = null)
    {
        CkId<CkRecordId> ckRecordId = new CkId<CkRecordId>(modelId, new CkRecordId($"{modelId}.{recordId}"));
        
        var d = attributes ?? new Dictionary<string, object?>();

        var record = new RtRecord(ckRecordId, d);



        return record;
    }

    #endregion
}
