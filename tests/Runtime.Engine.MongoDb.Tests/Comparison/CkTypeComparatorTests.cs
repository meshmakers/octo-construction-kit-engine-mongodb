using Meshmakers.Octo.ConstructionKit.Contracts;
using Meshmakers.Octo.ConstructionKit.Contracts.DataTransferObjects;
using Meshmakers.Octo.ConstructionKit.Contracts.DependencyGraph;
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
        var sourceTypes = new List<CkTypeGraph>();
        var targetTypes = new List<CkTypeGraph>();

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
        var sourceTypes = new List<CkTypeGraph>
        {
            CreateCkType("TestModel", "Type1")
        };
        var targetTypes = new List<CkTypeGraph>();

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
        var sourceTypes = new List<CkTypeGraph>();
        var targetTypes = new List<CkTypeGraph>
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
        var sourceTypes = new List<CkTypeGraph>
        {
            CreateCkType("TestModel", "Type1", isFinal: false, isAbstract: false)
        };
        var targetTypes = new List<CkTypeGraph>
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
        var sourceTypes = new List<CkTypeGraph>
        {
            CreateCkType("TestModel", "Type1", isFinal: true, isAbstract: false)
        };
        var targetTypes = new List<CkTypeGraph>
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
        var sourceTypes = new List<CkTypeGraph>
        {
            CreateCkType("TestModel", "Type1", isFinal: false, isAbstract: true)
        };
        var targetTypes = new List<CkTypeGraph>
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
    public void Compare_MultipleTypesWithMixedScenarios_CategorizesCorrectly()
    {
        // Arrange
        var sourceTypes = new List<CkTypeGraph>
        {
            CreateCkType("TestModel", "Type1"), // Only in source
            CreateCkType("TestModel", "Type2"), // In both, same
            CreateCkType("TestModel", "Type3", isFinal: true), // In both, different
        };
        var targetTypes = new List<CkTypeGraph>
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
        var sourceType = CreateCkType("TestModel", "Type1", attributeCount: 2);
        var targetType = CreateCkType("TestModel", "Type1", attributeCount: 1);

        var sourceTypes = new List<CkTypeGraph> { sourceType };
        var targetTypes = new List<CkTypeGraph> { targetType };

        // Act
        CkTypeComparison result = _comparator.Compare(sourceTypes, targetTypes);

        // Assert
        Assert.Single(result.Differences);
        Assert.Contains("Attributes count", result.Differences[0].Description);
    }

    #region Helper Methods

    /// <summary>
    ///     Creates a test CkTypeGraph with common defaults
    /// </summary>
    private CkTypeGraph CreateCkType(
        string modelId,
        string typeId,
        bool isFinal = false,
        bool isAbstract = false,
        string? description = null,
        bool isCollectionRoot = false,
        int attributeCount = 0)
    {
        CkId<CkTypeId> ckTypeId = new CkId<CkTypeId>(modelId, new CkTypeId($"{modelId}.{typeId}"));

        // Create attributes if requested
        var allAttributes = new Dictionary<CkId<CkAttributeId>, CkTypeAttributeGraph>();
        for (int i = 0; i < attributeCount; i++)
        {
            CkId<CkAttributeId> attrId = new CkId<CkAttributeId>(modelId, new CkAttributeId($"{modelId}.Attr{i + 1}"));
            var attrGraph = new CkTypeAttributeGraph(
                ckAttributeId: attrId,
                attributeName: $"Attr{i + 1}",
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

    #endregion
}
