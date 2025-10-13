using Meshmakers.Octo.ConstructionKit.Contracts;
using Meshmakers.Octo.Runtime.Contracts.MongoDb.Repositories.Entities;
using Meshmakers.Octo.Runtime.Engine.MongoDb.Comparison.Comparators;
using Meshmakers.Octo.Runtime.Engine.MongoDb.Comparison.Models;

using Xunit;

namespace Meshmakers.Octo.Runtime.Engine.MongoDb.Tests.Comparison;

public class CkTypeComparatorTests
{
    private readonly CkTypeComparator _comparator;

    public CkTypeComparatorTests()
    {
        _comparator = new CkTypeComparator();
    }

    [Fact]
    public void Compare_BothListsEmpty_ReturnsEmptyComparison()
    {
        // Arrange
        var sourceTypes = new List<CkType>();
        var targetTypes = new List<CkType>();

        // Act
        CkTypeComparison result = _comparator.Compare(sourceTypes, targetTypes);

        // Assert
        Assert.Empty(result.OnlyInSource);
        Assert.Empty(result.OnlyInTarget);
        Assert.Empty(result.InBothSame);
        Assert.Empty(result.Differences);
        Assert.Equal(0, result.TotalDifferences);
    }

    [Fact]
    public void Compare_TypeOnlyInSource_AddsToOnlyInSource()
    {
        // Arrange
        var sourceTypes = new List<CkType>
        {
            CreateCkType("TestModel", "Type1")
        };
        var targetTypes = new List<CkType>();

        // Act
        CkTypeComparison result = _comparator.Compare(sourceTypes, targetTypes);

        // Assert
        Assert.Single(result.OnlyInSource);
        Assert.Empty(result.OnlyInTarget);
        Assert.Empty(result.InBothSame);
        Assert.Empty(result.Differences);
        Assert.Equal(1, result.TotalDifferences);
        Assert.Equal("TestModel/TestModel.Type1", result.OnlyInSource[0].CkTypeId.ToString());
    }

    [Fact]
    public void Compare_TypeOnlyInTarget_AddsToOnlyInTarget()
    {
        // Arrange
        var sourceTypes = new List<CkType>();
        var targetTypes = new List<CkType>
        {
            CreateCkType("TestModel", "Type1")
        };

        // Act
        CkTypeComparison result = _comparator.Compare(sourceTypes, targetTypes);

        // Assert
        Assert.Empty(result.OnlyInSource);
        Assert.Single(result.OnlyInTarget);
        Assert.Empty(result.InBothSame);
        Assert.Empty(result.Differences);
        Assert.Equal(1, result.TotalDifferences);
        Assert.Equal("TestModel/TestModel.Type1", result.OnlyInTarget[0].CkTypeId.ToString());
    }

    [Fact]
    public void Compare_SameTypesInBoth_AddsToInBothSame()
    {
        // Arrange
        var sourceTypes = new List<CkType>
        {
            CreateCkType("TestModel", "Type1", isFinal: false, isAbstract: false)
        };
        var targetTypes = new List<CkType>
        {
            CreateCkType("TestModel", "Type1", isFinal: false, isAbstract: false)
        };

        // Act
        CkTypeComparison result = _comparator.Compare(sourceTypes, targetTypes);

        // Assert
        Assert.Empty(result.OnlyInSource);
        Assert.Empty(result.OnlyInTarget);
        Assert.Single(result.InBothSame);
        Assert.Empty(result.Differences);
        Assert.Equal(0, result.TotalDifferences);
        Assert.Equal("TestModel/TestModel.Type1", result.InBothSame[0].CkTypeId.ToString());
    }

    [Fact]
    public void Compare_TypesWithDifferentIsFinal_AddsToDifferences()
    {
        // Arrange
        var sourceTypes = new List<CkType>
        {
            CreateCkType("TestModel", "Type1", isFinal: true, isAbstract: false)
        };
        var targetTypes = new List<CkType>
        {
            CreateCkType("TestModel", "Type1", isFinal: false, isAbstract: false)
        };

        // Act
        CkTypeComparison result = _comparator.Compare(sourceTypes, targetTypes);

        // Assert
        Assert.Empty(result.OnlyInSource);
        Assert.Empty(result.OnlyInTarget);
        Assert.Empty(result.InBothSame);
        Assert.Single(result.Differences);
        Assert.Equal(1, result.TotalDifferences);
        Assert.Equal("TestModel/TestModel.Type1", result.Differences[0].CkTypeId);
        Assert.Contains("IsFinal", result.Differences[0].Description);
    }

    [Fact]
    public void Compare_TypesWithDifferentIsAbstract_AddsToDifferences()
    {
        // Arrange
        var sourceTypes = new List<CkType>
        {
            CreateCkType("TestModel", "Type1", isFinal: false, isAbstract: true)
        };
        var targetTypes = new List<CkType>
        {
            CreateCkType("TestModel", "Type1", isFinal: false, isAbstract: false)
        };

        // Act
        CkTypeComparison result = _comparator.Compare(sourceTypes, targetTypes);

        // Assert
        Assert.Empty(result.OnlyInSource);
        Assert.Empty(result.OnlyInTarget);
        Assert.Empty(result.InBothSame);
        Assert.Single(result.Differences);
        Assert.Equal(1, result.TotalDifferences);
        Assert.Equal("TestModel/TestModel.Type1", result.Differences[0].CkTypeId);
        Assert.Contains("IsAbstract", result.Differences[0].Description);
    }

    [Fact]
    public void Compare_TypesWithDifferentCollectionName_AddsToDifferences()
    {
        // Arrange
        var sourceTypes = new List<CkType>
        {
            CreateCkType("TestModel", "Type1", collectionName: "Collection1")
        };
        var targetTypes = new List<CkType>
        {
            CreateCkType("TestModel", "Type1", collectionName: "Collection2")
        };

        // Act
        CkTypeComparison result = _comparator.Compare(sourceTypes, targetTypes);

        // Assert
        Assert.Empty(result.OnlyInSource);
        Assert.Empty(result.OnlyInTarget);
        Assert.Empty(result.InBothSame);
        Assert.Single(result.Differences);
        Assert.Equal(1, result.TotalDifferences);
        Assert.Contains("CollectionName", result.Differences[0].Description);
    }

    [Fact]
    public void Compare_MultipleTypesWithMixedScenarios_CategorizesCorrectly()
    {
        // Arrange
        var sourceTypes = new List<CkType>
        {
            CreateCkType("TestModel", "Type1"), // Only in source
            CreateCkType("TestModel", "Type2"), // In both, same
            CreateCkType("TestModel", "Type3", isFinal: true), // In both, different
        };
        var targetTypes = new List<CkType>
        {
            CreateCkType("TestModel", "Type2"), // In both, same
            CreateCkType("TestModel", "Type3", isFinal: false), // In both, different
            CreateCkType("TestModel", "Type4"), // Only in target
        };

        // Act
        CkTypeComparison result = _comparator.Compare(sourceTypes, targetTypes);

        // Assert
        Assert.Single(result.OnlyInSource);
        Assert.Single(result.OnlyInTarget);
        Assert.Single(result.InBothSame);
        Assert.Single(result.Differences);
        Assert.Equal(3, result.TotalDifferences); // OnlyInSource + OnlyInTarget + Differences
        Assert.Equal("TestModel/TestModel.Type1", result.OnlyInSource[0].CkTypeId.ToString());
        Assert.Equal("TestModel/TestModel.Type4", result.OnlyInTarget[0].CkTypeId.ToString());
        Assert.Equal("TestModel/TestModel.Type2", result.InBothSame[0].CkTypeId.ToString());
        Assert.Equal("TestModel/TestModel.Type3", result.Differences[0].CkTypeId);
    }

    [Fact]
    public void Compare_TypesWithDifferentAttributeCount_AddsToDifferences()
    {
        // Arrange
        var sourceType = CreateCkType("TestModel", "Type1");
        sourceType.Attributes.Add(CreateCkTypeAttribute("Attr1"));
        sourceType.Attributes.Add(CreateCkTypeAttribute("Attr2"));

        var targetType = CreateCkType("TestModel", "Type1");
        targetType.Attributes.Add(CreateCkTypeAttribute("Attr1"));

        var sourceTypes = new List<CkType> { sourceType };
        var targetTypes = new List<CkType> { targetType };

        // Act
        CkTypeComparison result = _comparator.Compare(sourceTypes, targetTypes);

        // Assert
        Assert.Single(result.Differences);
        Assert.Contains("Attributes count", result.Differences[0].Description);
    }

    #region Helper Methods

    /// <summary>
    ///     Creates a test CkType with common defaults
    /// </summary>
    private CkType CreateCkType(
        string modelId,
        string typeId,
        bool isFinal = false,
        bool isAbstract = false,
        string? description = null,
        bool isCollectionRoot = false,
        string? collectionName = null,
        bool enableChangeStreamPreAndPostImages = false)
    {
        return new CkType
        {
            CkModelId = new CkModelId(modelId),
            CkTypeId = new CkId<CkTypeId>(modelId, new CkTypeId($"{modelId}.{typeId}")),
            IsFinal = isFinal,
            IsAbstract = isAbstract,
            Description = description,
            IsCollectionRoot = isCollectionRoot,
            CollectionName = collectionName ?? $"{typeId}Collection",
            EnableChangeStreamPreAndPostImages = enableChangeStreamPreAndPostImages,
            Attributes = new HashSet<CkTypeAttribute>(),
            Indexes = new HashSet<CkTypeIndex>()
        };
    }

    /// <summary>
    ///     Creates a test CkTypeAttribute
    /// </summary>
    private CkTypeAttribute CreateCkTypeAttribute(string attributeName)
    {
        return new CkTypeAttribute
        {
            AttributeId = new CkId<CkAttributeId>("TestModel", new CkAttributeId($"TestModel.{attributeName}")),
            AttributeName = attributeName,
            IsOptional = false
        };
    }

    #endregion
}
