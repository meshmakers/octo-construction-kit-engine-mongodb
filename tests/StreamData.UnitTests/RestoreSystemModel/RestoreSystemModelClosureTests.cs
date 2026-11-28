using Meshmakers.Octo.ConstructionKit.Contracts;
using Meshmakers.Octo.ConstructionKit.Contracts.DataTransferObjects;
using Meshmakers.Octo.Runtime.Engine.MongoDb.Services.RestoreSystemModelCheck;

namespace Meshmakers.Octo.Runtime.Engine.MongoDb.UnitTests.RestoreSystemModel;

/// <summary>
///     AB#10061: the resolved dependency closure of a range-retaining model is a build artefact. The restore check accepts
///     a closure that moved within the declared ranges (and leaves the published check to the guard), and keeps comparing
///     everything else exactly - the write restriction, the declared ranges and the closure of a classic model.
/// </summary>
public class RestoreSystemModelClosureTests
{
    private static CkCompiledModelRoot Model(string[] closure, bool rangeRetaining = true, bool writeAccess = true,
        string floor = "2.5.0", string range = "[2.5,3.0)") => new()
    {
        ModelId = new CkModelId("System.UI-2.12.0"),
        Description = "ui",
        Dependencies = closure.Select(c => new CkModelId(c)).ToList(),
        DependencyRanges = rangeRetaining
            ?
            [
                new CkModelDependencyDto
                {
                    Range = new CkModelIdVersionRange("System", range), Floor = floor,
                    UsedSurface = ["System@2/Entity-1"]
                }
            ]
            : null,
        Types =
        [
            new CkCompiledTypeDto
            {
                TypeId = new CkTypeId("Dashboard-1"),
                WriteAccess = writeAccess ? new CkTypeWriteAccessDto { Principals = [CkWritePrincipals.Interactive] } : null
            }
        ]
    };

    private static RestoreSystemModelContentComparison Compare(CkCompiledModelRoot readBack,
        CkCompiledModelRoot published) => RestoreSystemModelGuard.CompareContent(readBack, published);

    [Fact]
    public void SameClosure_IsIdentical()
    {
        var result = Compare(Model(["System-2.8.0"]), Model(["System-2.8.0"]));

        Assert.Equal(RestoreSystemModelContentMatch.Identical, result.Match);
        Assert.Empty(result.MovedClosureEntries);
    }

    [Theory]
    [InlineData("System-2.10.0")] // a service built after the first publish
    [InlineData("System-2.5.0")] // a build against an older catalog, at the floor
    public void ClosureMovedWithinTheDeclaredRange_IsGenuine_AndNamesTheMovedEntry(string entry)
    {
        var result = Compare(Model([entry]), Model(["System-2.8.0"]));

        Assert.Equal(RestoreSystemModelContentMatch.ClosureMoved, result.Match);
        Assert.Equal([new CkModelId(entry)], result.MovedClosureEntries);
        Assert.True(RestoreSystemModelGuard.IsSameContent(Model([entry]), Model(["System-2.8.0"])));
    }

    [Theory]
    [InlineData("System-3.0.0")] // another major
    [InlineData("System-2.4.0")] // below the floor
    [InlineData("System@2")] // not a concrete version
    public void ClosureEntryOutsideTheDeclaredRange_IsAnotherContent(string entry)
    {
        Assert.Equal(RestoreSystemModelContentMatch.Differs, Compare(Model([entry]), Model(["System-2.8.0"])).Match);
    }

    [Fact]
    public void ClosureEntryBelowARaisedFloor_IsAnotherContent()
    {
        var published = Model(["System-2.8.0"], floor: "2.7.0");

        Assert.Equal(RestoreSystemModelContentMatch.Differs,
            Compare(Model(["System-2.6.0"], floor: "2.7.0"), published).Match);
        Assert.Equal(RestoreSystemModelContentMatch.ClosureMoved,
            Compare(Model(["System-2.7.0"], floor: "2.7.0"), published).Match);
    }

    [Fact]
    public void ClosureWithAnAddedMissingOrRepeatedName_IsAnotherContent()
    {
        var published = Model(["System-2.8.0"]);

        Assert.Equal(RestoreSystemModelContentMatch.Differs,
            Compare(Model(["System-2.8.0", "Basic-2.7.0"]), published).Match);
        Assert.Equal(RestoreSystemModelContentMatch.Differs, Compare(Model([]), published).Match);
        Assert.Equal(RestoreSystemModelContentMatch.Differs,
            Compare(Model(["System-2.8.0", "System-2.10.0"]), published).Match);
    }

    [Fact]
    public void MovedClosure_DoesNotHideAnyOtherChange()
    {
        var published = Model(["System-2.8.0"]);
        var noWriteAccess = Model(["System-2.10.0"], writeAccess: false);
        var otherFloor = Model(["System-2.10.0"], floor: "2.6.0");
        var otherRange = Model(["System-2.10.0"], range: "[2.5,4.0)");
        var otherSurface = Model(["System-2.10.0"]);
        otherSurface.DependencyRanges![0].UsedSurface = ["System@2/Entity-1", "System@2/Name-1"];

        Assert.Equal(RestoreSystemModelContentMatch.Differs, Compare(noWriteAccess, published).Match);
        Assert.Equal(RestoreSystemModelContentMatch.Differs, Compare(otherFloor, published).Match);
        Assert.Equal(RestoreSystemModelContentMatch.Differs, Compare(otherRange, published).Match);
        Assert.Equal(RestoreSystemModelContentMatch.Differs, Compare(otherSurface, published).Match);
    }

    [Fact]
    public void ClassicModel_ResolvesByItsPins_SoItsClosureIsComparedExactly()
    {
        var published = Model(["System-2.8.0"], rangeRetaining: false);

        Assert.Equal(RestoreSystemModelContentMatch.Differs,
            Compare(Model(["System-2.10.0"], rangeRetaining: false), published).Match);
        Assert.Equal(RestoreSystemModelContentMatch.Differs,
            Compare(Model(["System-2.10.0"]), published).Match);
    }

    [Fact]
    public void TransitiveClosureEntryWithoutADeclaredRange_MovesWithinItsMajorOnly()
    {
        var published = Model(["System-2.8.0", "System.Bot-3.9.0"]);

        Assert.Equal(RestoreSystemModelContentMatch.ClosureMoved,
            Compare(Model(["System-2.8.0", "System.Bot-3.4.0"]), published).Match);
        Assert.Equal(RestoreSystemModelContentMatch.Differs,
            Compare(Model(["System-2.8.0", "System.Bot-4.0.0"]), published).Match);
    }
}
