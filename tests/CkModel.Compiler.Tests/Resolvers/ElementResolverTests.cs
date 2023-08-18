using Meshmakers.Octo.SystematizedData.CkModel.Compiler.Resolvers;
using Meshmakers.Octo.SystematizedData.CkModel.Contracts;
using Meshmakers.Octo.SystematizedData.CkModel.Contracts.DataTransferObjects;
using Meshmakers.Octo.SystematizedData.CkModel.Contracts.DependencyGraph;
using Meshmakers.Octo.SystematizedData.CkModel.Contracts.Messages;

namespace CkModel.Compiler.Tests.Resolvers;

public class ElementResolverTests
{
    [Fact]
    public void Resolve_ValidInput_ReturnsCkModelGraph()
    {
        var ckModelRoot = sampleData.sample1.Builder.Build();
        
        var resolver = new ElementResolver();
        var validationResult = new CompilerResult();

        var result = resolver.Resolve(ckModelRoot, validationResult);

        Assert.IsType<CkModelGraph>(result);
    }

    [Fact]
    public void Resolve_InvalidAttributeName_AddsErrorMessage()
    {
        var ckModelRoot = sampleData.sample1.Builder.Build();
        if (ckModelRoot.CkAttributes != null)
        {
            ckModelRoot.CkAttributes[0].AttributeId = "Invalid_Attribute_Name!";
        }

        var resolver = new ElementResolver();
        var validationResult = new CompilerResult();
    
        resolver.Resolve(ckModelRoot, validationResult);
    
        Assert.Single(validationResult.Messages);
        Assert.Equal(MessageLevel.Error, validationResult.Messages[0].MessageLevel);
        Assert.Equal(25, validationResult.Messages[0].MessageNumber);
    }

    [Fact]
    public void Resolve_InvalidAssociationRoleId_AddsErrorMessage()
    {
        var ckModelRoot = sampleData.sample1.Builder.Build();
        if (ckModelRoot.CkAssociationRoles != null)
        {
            ckModelRoot.CkAssociationRoles[0].AssociationRoleId = "Invalid_Assoc_Role!";
        }

        var resolver = new ElementResolver();
        var validationResult = new CompilerResult();
    
        resolver.Resolve(ckModelRoot, validationResult);
    
        Assert.Single(validationResult.Messages);
        Assert.Equal(MessageLevel.Error, validationResult.Messages[0].MessageLevel);
        Assert.Equal(26, validationResult.Messages[0].MessageNumber);
    }
    
    [Fact]
    public void Resolve_InvalidTypeId_AddsErrorMessage()
    {
        var ckModelRoot = sampleData.sample1.Builder.Build();
        if (ckModelRoot.CkTypes != null)
        {
            ckModelRoot.CkTypes[0].TypeId = "Invalid_TypeId!";
        }

        var resolver = new ElementResolver();
        var validationResult = new CompilerResult();
    
        resolver.Resolve(ckModelRoot, validationResult);
    
        Assert.Single(validationResult.Messages);
        Assert.Equal(MessageLevel.Error, validationResult.Messages[0].MessageLevel);
        Assert.Equal(24, validationResult.Messages[0].MessageNumber);
    }
    
    [Fact]
    public void Resolve_MultipleTypes_AddsErrorMessage()
    {
        var ckModelRoot = sampleData.sample1.Builder.Build();
        if (ckModelRoot.CkTypes != null)
        {
            ckModelRoot.CkTypes.Add(new CkTypeDto{TypeId = "Demo1"});
        }

        var resolver = new ElementResolver();
        var validationResult = new CompilerResult();
    
        resolver.Resolve(ckModelRoot, validationResult);
    
        Assert.Single(validationResult.Messages);
        Assert.Equal(MessageLevel.Error, validationResult.Messages[0].MessageLevel);
        Assert.Equal(8, validationResult.Messages[0].MessageNumber);
    }
    
    [Fact]
    public void Resolve_MultipleAssociations_AddsErrorMessage()
    {
        var ckModelRoot = sampleData.sample1.Builder.Build();
        if (ckModelRoot.CkAttributes != null)
        {
            ckModelRoot.CkAttributes.Add(new CkAttributeDto{AttributeId = "Demo1"});
            ckModelRoot.CkAttributes.Add(new CkAttributeDto{AttributeId = "Demo1"});
        }

        var resolver = new ElementResolver();
        var validationResult = new CompilerResult();
    
        resolver.Resolve(ckModelRoot, validationResult);
    
        Assert.Single(validationResult.Messages);
        Assert.Equal(MessageLevel.Error, validationResult.Messages[0].MessageLevel);
        Assert.Equal(6, validationResult.Messages[0].MessageNumber);
    }
    
    [Fact]
    public void Resolve_MultipleAttributes_AddsErrorMessage()
    {
        var ckModelRoot = sampleData.sample1.Builder.Build();
        ckModelRoot.CkAssociationRoles = new List<CkAssociationRoleDto>
        {
            new() { AssociationRoleId = "Assoc1" },
            new() { AssociationRoleId = "Assoc1" }
        };

        var resolver = new ElementResolver();
        var validationResult = new CompilerResult();
    
        resolver.Resolve(ckModelRoot, validationResult);
    
        Assert.Single(validationResult.Messages);
        Assert.Equal(MessageLevel.Error, validationResult.Messages[0].MessageLevel);
        Assert.Equal(7, validationResult.Messages[0].MessageNumber);
    }
}