using Meshmakers.Octo.ConstructionKit.Contracts;
using Meshmakers.Octo.Runtime.Engine.MongoDb.Repositories.MongoDb;

namespace Meshmakers.Octo.Runtime.Engine.MongoDb.UnitTests;

/// <summary>
///     CK v2 F1.0 pure logic: the embedded-import decision table (AB#5900, plan §3.2) and the failure propagation of
///     the post-import re-validation (AB#5901). The database behaviour is covered by
///     <c>CkModelImportGuardTests</c> in the integration tests.
/// </summary>
public class CkModelImportGuardDecisionTests
{
    [Theory]
    [InlineData("System-2.5.0", null, "Import")]
    [InlineData("System-2.5.0", "System-2.4.0", "Import")]
    [InlineData("System-2.5.0", "System-2.5.0", "AlreadyInstalled")]
    [InlineData("System-2.5.0", "System-2.5.1", "SkipNewerInstalled")]
    [InlineData("System-2.5.0", "System-2.6.0", "SkipNewerInstalled")]
    [InlineData("System-2.5.0", "System-3.0.0", "SkipNewerMajorInstalled")]
    [InlineData("System-2.5.0", "System-1.9.0", "Import")]
    public void Decide_FollowsTheDecisionTable(string embedded, string? installed, string expected)
    {
        Assert.Equal(Enum.Parse<EmbeddedImportDecision>(expected),
            EmbeddedCkModelImportGuard.Decide(new CkModelId(embedded), installed == null ? null : new CkModelId(installed)));
    }

    [Fact]
    public void ComputeFailedModels_ExactPinNotInstalled_FailsWithInstalledVersionInTheReason()
    {
        var failed = DatabaseCkModelRepository.ComputeFailedModels(
        [
            Model("System-2.6.0"),
            Model("Basic-1.0.0", "System-2.5.0")
        ], [], []);

        var reason = Assert.Single(failed).Value;
        Assert.Contains("2.5.0", reason);
        Assert.Contains("installed: System-2.6.0", reason);
    }

    [Fact]
    public void ComputeFailedModels_PinSatisfied_NothingFails()
    {
        var failed = DatabaseCkModelRepository.ComputeFailedModels(
        [
            Model("System-2.5.0"),
            Model("Basic-1.0.0", "System-2.5.0"),
            Model("Basic.Accounting-1.0.0", "Basic-1.0.0", "System-2.5.0")
        ], [], []);

        Assert.Empty(failed);
    }

    /// <summary>
    ///     A model whose dependency fails only its inheritance (visible to the resolver during re-validation) is
    ///     failed too, transitively — otherwise it would stay Available on an unusable dependency.
    /// </summary>
    [Fact]
    public void ComputeFailedModels_PropagatesToDependentsTransitively()
    {
        var failed = DatabaseCkModelRepository.ComputeFailedModels(
        [
            Model("System-2.5.0"),
            Model("Basic-1.0.0", "System-2.5.0"),
            Model("Basic.Accounting-1.0.0", "Basic-1.0.0"),
            Model("Reports-1.0.0", "Basic.Accounting-1.0.0"),
            Model("Unrelated-1.0.0", "System-2.5.0")
        ], [], [new CkModelId("Basic-1.0.0")]);

        Assert.Equal(["Basic-1.0.0", "Basic.Accounting-1.0.0", "Reports-1.0.0"],
            failed.Keys.Select(k => k.FullName).Order(StringComparer.Ordinal));
        Assert.Contains("inheritance", failed[new CkModelId("Basic-1.0.0")]);
        Assert.Contains("'Basic-1.0.0' is ResolveFailed", failed[new CkModelId("Basic.Accounting-1.0.0")]);
    }

    /// <summary>
    ///     Review I2: a range-retaining model is judged by its range (+ floor), not by the exact pin of its compile:
    ///     an additive System minor does not fail it, a version below the floor does.
    /// </summary>
    [Fact]
    public void ComputeFailedModels_RangeRequirement_SatisfiedByANewerMinor_FailsBelowTheFloor()
    {
        var range = new CkModelIdVersionRange("System", "[2.5.0,3.0.0)");
        var failed = DatabaseCkModelRepository.ComputeFailedModels(
        [
            Model("System-2.6.0"),
            (new CkModelId("RangeDep-1.0.0"), [range])
        ], [], []);
        Assert.Empty(failed);

        failed = DatabaseCkModelRepository.ComputeFailedModels(
        [
            Model("System-2.4.0"),
            (new CkModelId("RangeDep-1.0.0"), [range])
        ], [], []);
        Assert.Contains("installed: System-2.4.0", Assert.Single(failed).Value);
    }

    private static (CkModelId, IReadOnlyCollection<CkModelIdVersionRange>) Model(string id, params string[] exactPins) =>
        (new CkModelId(id), exactPins.Select(p => new CkModelId(p).ToVersionRange()).ToList());
}
