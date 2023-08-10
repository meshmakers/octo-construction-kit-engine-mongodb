using FakeItEasy;
using Meshmakers.Octo.SystematizedData.CkModel.Compiler;
using Meshmakers.Octo.SystematizedData.CkModel.Contracts.DependencyGraph;
using Meshmakers.Octo.SystematizedData.Persistence.CkModel.CkRuleEngine;
using Microsoft.Extensions.Logging;

namespace CkModel.Compiler.Tests;

public class InheritanceResolverTests
{
    [Fact]
    public void MissingInheritanceEntity_OK()
    {
        var logger = A.Fake<ILogger<InheritanceResolver> >();

        CkAggregatedModelElements ckAggregatedModelElements = new();
        ckAggregatedModelElements.AppendModel(sampleData.systemFake.Builder.Build());
        ckAggregatedModelElements.AppendModel(sampleData.sample1.Builder.Build());

        InheritanceResolver inheritanceResolver = new(logger);
        var graph = inheritanceResolver.ResolveInheritanceAsync(ckAggregatedModelElements);
        
        Assert.Equal(4, graph.Entities.Count);
        Assert.NotNull(graph.Entities["System/Entity"]);
        Assert.NotNull(graph.Entities["sample1/Demo1"]);
        Assert.NotNull(graph.Entities["sample1/Demo2"]);
        Assert.Equal(0, graph.Entities["System/Entity"].Attributes.Count);
        Assert.Equal(1, graph.Entities["System/Entity"].Associations.In.Owned.Count);
        Assert.Contains(graph.Entities["System/Entity"].Associations.In.Owned, a=> a.RoleId == "sample1/Related");
        Assert.Equal(0, graph.Entities["System/Entity"].Associations.In.Inherited.Count);
        Assert.Equal(0, graph.Entities["System/Entity"].Associations.Out.Owned.Count);
        Assert.Equal(0, graph.Entities["System/Entity"].Associations.Out.Inherited.Count);
        
        Assert.Equal(3, graph.Entities["sample1/Demo1"].Attributes.Count);
        Assert.Equal(1, graph.Entities["sample1/Demo1"].Associations.In.Owned.Count);
        Assert.Contains(graph.Entities["sample1/Demo1"].Associations.In.Owned, a=> a.RoleId == "System/ParentChild");
        Assert.Equal(1, graph.Entities["sample1/Demo1"].Associations.In.Inherited.Count);
        Assert.Contains(graph.Entities["sample1/Demo1"].Associations.In.Inherited, a=> a.RoleId == "sample1/Related");
        Assert.Equal(0, graph.Entities["sample1/Demo1"].Associations.Out.Inherited.Count);
        Assert.Equal(0, graph.Entities["sample1/Demo1"].Associations.Out.Owned.Count);
        
        Assert.Equal(6, graph.Entities["sample1/Demo2"].Attributes.Count);
        Assert.Equal(1, graph.Entities["sample1/Demo2"].Associations.In.Inherited.Count);
        Assert.Contains(graph.Entities["sample1/Demo2"].Associations.In.Inherited, a=> a.RoleId == "sample1/Related");
        Assert.Equal(0, graph.Entities["sample1/Demo2"].Associations.In.Owned.Count);
        Assert.Equal(0, graph.Entities["sample1/Demo2"].Associations.Out.Inherited.Count);
        Assert.Equal(1, graph.Entities["sample1/Demo2"].Associations.Out.Owned.Count);
        Assert.Contains(graph.Entities["sample1/Demo2"].Associations.Out.Owned, a=> a.RoleId == "System/ParentChild");
    }
    
    [Fact]
    public void MissingInheritanceType_ThrowsException()
    {
        var logger = A.Fake<ILogger<InheritanceResolver> >();

        CkAggregatedModelElements ckAggregatedModelElements = new();
        ckAggregatedModelElements.AppendModel(sampleData.sample1.Builder.Build());

        InheritanceResolver inheritanceResolver = new(logger);
        Assert.Throws<ModelValidationException>(() => inheritanceResolver.ResolveInheritanceAsync(ckAggregatedModelElements));
    }
}