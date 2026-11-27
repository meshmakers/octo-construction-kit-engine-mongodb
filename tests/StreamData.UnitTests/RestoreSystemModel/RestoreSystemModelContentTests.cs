using Meshmakers.Octo.ConstructionKit.Contracts;
using Meshmakers.Octo.ConstructionKit.Contracts.DataTransferObjects;
using Meshmakers.Octo.Runtime.Engine.MongoDb.Services.RestoreSystemModelCheck;

namespace Meshmakers.Octo.Runtime.Engine.MongoDb.UnitTests.RestoreSystemModel;

/// <summary>
///     The content comparison of the restore check: a restored model is genuine only when its canonical form equals the
///     catalog's. Row order, nulls versus empty lists and the scalar text of default values do not count; every model
///     member does, the write restriction above all.
/// </summary>
public class RestoreSystemModelContentTests
{
    private static CkCompiledModelRoot Model(bool writeAccess = true, bool reversed = false,
        IEnumerable<CkEnumValueDto>? extraValues = null, bool extensible = true)
    {
        var types = new List<CkCompiledTypeDto>
        {
            new()
            {
                TypeId = new CkTypeId("User-1"),
                Description = "user",
                WriteAccess = writeAccess
                    ? new CkTypeWriteAccessDto { Principals = [CkWritePrincipals.Interactive] }
                    : null
            },
            new() { TypeId = new CkTypeId("Role-1"), Description = "role" }
        };
        if (reversed)
        {
            types.Reverse();
        }

        var values = new List<CkEnumValueDto> { new() { Key = 0, Name = "Active" }, new() { Key = 1, Name = "Locked" } };
        values.AddRange(extraValues ?? []);
        return new CkCompiledModelRoot
        {
            ModelId = new CkModelId("System.Identity-3.8.0"),
            Description = "identity",
            Dependencies = [new CkModelId("System-2.0.0")],
            Types = types,
            Enums = [new CkEnumDto { EnumId = new CkEnumId("UserState-1"), IsExtensible = extensible, Values = values }],
            Attributes =
            [
                new CkAttributeDto
                {
                    AttributeId = new CkAttributeId("Count-1"), ValueType = AttributeValueTypesDto.Int,
                    DefaultValues = ["0"]
                }
            ]
        };
    }

    [Fact]
    public void SameModel_RowsInAnotherOrder_IsTheSameContent()
    {
        Assert.True(RestoreSystemModelGuard.IsSameContent(Model(reversed: true), Model()));
        Assert.Equal(CkCompiledModelCanonicalizer.ContentHash(Model(reversed: true)),
            CkCompiledModelCanonicalizer.ContentHash(Model()));
    }

    [Fact]
    public void ScalarDefaultValue_ComparedByItsText()
    {
        var readBack = Model();
        readBack.Attributes!.Single().DefaultValues = [0];

        Assert.True(RestoreSystemModelGuard.IsSameContent(readBack, Model()));
    }

    [Fact]
    public void RemovedWriteAccess_IsAnotherContent()
    {
        Assert.False(RestoreSystemModelGuard.IsSameContent(Model(writeAccess: false), Model()));
        Assert.NotEqual(CkCompiledModelCanonicalizer.ContentHash(Model(writeAccess: false)),
            CkCompiledModelCanonicalizer.ContentHash(Model()));
    }

    [Fact]
    public void ChangedDescriptionOrAddedType_IsAnotherContent()
    {
        var described = Model();
        described.Description = "self-built";
        var extended = Model();
        extended.Types!.Add(new CkCompiledTypeDto { TypeId = new CkTypeId("Backdoor-1") });

        Assert.False(RestoreSystemModelGuard.IsSameContent(described, Model()));
        Assert.False(RestoreSystemModelGuard.IsSameContent(extended, Model()));
    }

    [Fact]
    public void ValuesATenantAddedToAnExtensibleEnum_AreNotPublishedContent()
    {
        var customized = Model(extraValues: [new CkEnumValueDto { Key = 7, Name = "Custom", IsExtension = true }]);

        Assert.True(RestoreSystemModelGuard.IsSameContent(customized, Model()));
    }

    [Fact]
    public void ExtraValuesOfANonExtensibleEnum_OrNotMarkedAsExtension_AreAnotherContent()
    {
        var nonExtensible = Model(extensible: false,
            extraValues: [new CkEnumValueDto { Key = 7, Name = "Custom", IsExtension = true }]);
        var unmarked = Model(extraValues: [new CkEnumValueDto { Key = 7, Name = "Custom" }]);

        Assert.False(RestoreSystemModelGuard.IsSameContent(nonExtensible, Model(extensible: false)));
        Assert.False(RestoreSystemModelGuard.IsSameContent(unmarked, Model()));
    }

    [Fact]
    public void Migrations_SchemaAndCompatibility_AreNotPersistedAndNotCompared()
    {
        Assert.Contains("$.migrations", CkCompiledModelCanonicalizer.IgnoredPaths.Keys);
        Assert.Contains("$.$schema", CkCompiledModelCanonicalizer.IgnoredPaths.Keys);
        Assert.Contains("$.compatibility", CkCompiledModelCanonicalizer.IgnoredPaths.Keys);
        Assert.Equal(3, CkCompiledModelCanonicalizer.IgnoredPaths.Count);
    }

    [Fact]
    public void ContentHash_IsHexAndCarriesNoContent()
    {
        var hash = CkCompiledModelCanonicalizer.ContentHash(Model());

        Assert.Matches("^[0-9a-f]{64}$", hash);
    }
}
