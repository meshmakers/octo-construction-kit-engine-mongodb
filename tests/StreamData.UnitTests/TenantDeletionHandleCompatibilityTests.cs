using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Meshmakers.Octo.ConstructionKit.Contracts;
using Meshmakers.Octo.Runtime.Contracts.MongoDb;
using Xunit;

namespace Meshmakers.Octo.Runtime.Engine.UnitTests;

// A package compiled against the engine-mongodb before TenantDeletionHandle got StreamDataModelPresent calls the
// 3-argument constructor and the 3-output Deconstruct by their compiled signature; both must stay, or the call fails
// with a MissingMethodException when old and new packages are loaded together.
public class TenantDeletionHandleCompatibilityTests
{
    [Fact]
    public void TheShapeBeforeStreamDataModelPresent_IsStillThere_AndMeansNoStreamDataModel()
    {
        var constructor = typeof(TenantDeletionHandle).GetConstructor(
            [typeof(string), typeof(Guid), typeof(IReadOnlyList<OctoObjectId>)]);
        Assert.NotNull(constructor);

        var archives = new List<OctoObjectId> { OctoObjectId.GenerateNewId() };
        var correlationId = Guid.NewGuid();
        var handle = (TenantDeletionHandle)constructor.Invoke(["db", correlationId, archives]);

        Assert.False(handle.StreamDataModelPresent);
        Assert.Equal(archives, handle.StreamDataArchives);

        var deconstruct = typeof(TenantDeletionHandle).GetMethods(BindingFlags.Public | BindingFlags.Instance)
            .Single(m => m.Name == "Deconstruct" && m.GetParameters().Length == 3);
        var arguments = new object?[] { null, null, null };
        deconstruct.Invoke(handle, arguments);
        Assert.Equal(["db", correlationId, archives], arguments);
    }

    [Fact]
    public void TheFourArgumentShape_KeepsWorking()
    {
        var handle = new TenantDeletionHandle("db", Guid.NewGuid(), [], true);

        Assert.True(handle.StreamDataModelPresent);
        var (name, _, _, present) = handle;
        Assert.Equal("db", name);
        Assert.True(present);
    }
}
