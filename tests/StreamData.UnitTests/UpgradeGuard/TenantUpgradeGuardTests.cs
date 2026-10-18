using System.Diagnostics;
using Meshmakers.Octo.ConstructionKit.Contracts;
using Meshmakers.Octo.ConstructionKit.Contracts.DataTransferObjects;
using Meshmakers.Octo.Runtime.Contracts.MongoDb.UpgradeGuard;
using Meshmakers.Octo.Runtime.Engine.MongoDb.UpgradeGuard;

namespace Meshmakers.Octo.Runtime.Engine.MongoDb.UnitTests.UpgradeGuard;

/// <summary>
///     Tenant upgrade guard (CK v2 F2.5, AB#6867). The guard runs against the real surface checker, diff service and
///     classifier of the CK engine; only the tenant's installed models are faked (read-only).
/// </summary>
public class TenantUpgradeGuardTests
{
    private static Task<CkUpgradeGuardResult> Evaluate(FakeInstalledModels installed, params CkCompiledModelRoot[] updateSet) =>
        GuardModels.Guard().EvaluateAsync(installed, updateSet, null, CancellationToken.None);

    private static CkCompiledModelRoot Plain(string id, params string[] pins) => GuardModels.Classic(id, pins);

    /// <summary>The sbeg tenant of 2026-10-17 (staging-1): Registration 2.8.0 exact-pins the models LibraryStatus offers to update.</summary>
    private static FakeInstalledModels SbegTenant()
    {
        var installed = new FakeInstalledModels()
            .Install(Plain("Basic-2.4.0"))
            .Install(Plain("Basic.Energy-1.9.0", "Basic-2.4.0"))
            .Install(Plain("EnergyCommunity-4.9.0", "Basic-2.4.0", "Basic.Energy-1.9.0"))
            .Install(Plain("EnergyCommunity.Registration-2.8.0", "Basic-2.4.0", "Basic.Energy-1.9.0",
                "EnergyCommunity-4.9.0"));
        return installed;
    }

    private static CkCompiledModelRoot[] SbegUpdateSet() =>
    [
        Plain("Basic-2.5.0"),
        Plain("Basic.Energy-1.10.0", "Basic-2.5.0"),
        Plain("EnergyCommunity-4.10.0", "Basic-2.5.0", "Basic.Energy-1.10.0")
    ];

    [Fact]
    public async Task SbegCase_ExactPinnedRegistration_YieldsOneExactPinBlockerPerPin()
    {
        var result = await Evaluate(SbegTenant(), SbegUpdateSet());

        Assert.True(result.IsBlocked);
        Assert.Equal(3, result.Blockers.Count);
        Assert.All(result.Blockers, b =>
        {
            Assert.Equal(CkUpgradeBlockerKind.ExactPin, b.Kind);
            Assert.Equal("EnergyCommunity.Registration-2.8.0", b.Dependent.FullName);
            Assert.Equal("CK_UPGRADE_EXACT_PIN", b.Code);
            Assert.False(b.DependentIsInUpdateSet);
        });
        Assert.Equal(["Basic-2.4.0", "Basic.Energy-1.9.0", "EnergyCommunity-4.9.0"],
            result.Blockers.Select(b => b.Subject).OrderBy(s => s, StringComparer.Ordinal));
        var basic = result.Blockers.Single(b => b.Subject == "Basic-2.4.0");
        Assert.Equal("Basic-2.5.0", basic.Update.FullName);
        Assert.Contains("exact pin Basic-2.4.0", basic.Message);
        Assert.Contains("Basic-2.5.0", basic.Message);
    }

    [Fact]
    public async Task SbegCase_SingleCandidate_BlocksOnlyThePinOfThatModel()
    {
        var result = await Evaluate(SbegTenant(), Plain("Basic-2.5.0"));

        var blocker = Assert.Single(result.Blockers, b => b.Dependent.Name == "EnergyCommunity.Registration");
        Assert.Equal("Basic-2.4.0", blocker.Subject);
        // Basic.Energy and EnergyCommunity are exact-pinned to Basic-2.4.0 as well: they break just the same.
        Assert.Contains(result.Blockers, b => b.Dependent.Name == "Basic.Energy");
        Assert.Contains(result.Blockers, b => b.Dependent.Name == "EnergyCommunity");
    }

    [Fact]
    public async Task SbegCase_BlockersCarryTheOfferedNewerVersionOfTheDependentAsSuggestion()
    {
        var result = await GuardModels.Guard().EvaluateAsync(SbegTenant(), SbegUpdateSet(),
            [new CkModelId("EnergyCommunity.Registration-2.10.0"), new CkModelId("EnergyCommunity.Registration-2.7.0")],
            CancellationToken.None);

        Assert.All(result.Blockers, b => Assert.Equal("EnergyCommunity.Registration-2.10.0", b.SuggestedDependentUpdate?.FullName));
    }

    [Fact]
    public async Task RangeRetainingRegistration_SameUpdateSet_NoBlocker()
    {
        var registration = GuardModels.RangeDependent("EnergyCommunity.Registration-2.10.0");
        var installed = new FakeInstalledModels()
            .Install(GuardModels.Basic("2.4.0"))
            .Install(registration);

        var result = await Evaluate(installed, GuardModels.Basic("2.5.0", extraAssetAttribute: "Extra"));

        Assert.False(result.IsBlocked);
        Assert.Empty(result.Blockers);
    }

    [Fact]
    public async Task ExactPinnedDependentReplacedInTheSetByARangeRetainingVersion_IsNotReported()
    {
        var installed = new FakeInstalledModels()
            .Install(GuardModels.Basic("2.4.0"))
            .Install(Plain("EnergyCommunity.Registration-2.8.0", "Basic-2.4.0"));

        var result = await Evaluate(installed, GuardModels.Basic("2.5.0"),
            GuardModels.RangeDependent("EnergyCommunity.Registration-2.10.0"));

        Assert.Empty(result.Blockers);
    }

    [Fact]
    public async Task ExactPinnedDependentReplacedInTheSetByAnotherExactPinnedVersion_IsReportedAsPartOfTheSet()
    {
        // Registration 2.9.0 still pins Basic-2.4.0: the post-state is broken, and the broken model is in the set.
        var installed = new FakeInstalledModels()
            .Install(GuardModels.Basic("2.4.0"))
            .Install(Plain("EnergyCommunity.Registration-2.8.0", "Basic-2.4.0"));

        var result = await Evaluate(installed, GuardModels.Basic("2.5.0"), Plain("EnergyCommunity.Registration-2.9.0", "Basic-2.4.0"));

        var blocker = Assert.Single(result.Blockers);
        Assert.Equal("EnergyCommunity.Registration-2.9.0", blocker.Dependent.FullName);
        Assert.True(blocker.DependentIsInUpdateSet);
        Assert.Null(blocker.SuggestedDependentUpdate);
    }

    [Fact]
    public async Task ExactPinnedDependentReplacedInTheSetByAVersionPinnedToTheNewCandidate_IsNotReported()
    {
        var installed = new FakeInstalledModels()
            .Install(GuardModels.Basic("2.4.0"))
            .Install(Plain("Registration-2.8.0", "Basic-2.4.0"));

        var result = await Evaluate(installed, GuardModels.Basic("2.5.0"), Plain("Registration-2.9.0", "Basic-2.5.0"));

        Assert.Empty(result.Blockers);
    }

    [Fact]
    public async Task RangeRetainingDependent_UsedElementRemoved_ReturnsElementRemovedWithTheElement()
    {
        var installed = new FakeInstalledModels()
            .Install(GuardModels.Basic("2.4.0"))
            .Install(GuardModels.RangeDependent("Reg-2.10.0"));

        var result = await Evaluate(installed, GuardModels.Basic("2.5.0", withAsset: false));

        var blocker = Assert.Single(result.Blockers);
        Assert.Equal(CkUpgradeBlockerKind.ElementRemoved, blocker.Kind);
        Assert.Equal("CK_UPGRADE_ELEMENT_REMOVED", blocker.Code);
        Assert.Equal("Reg-2.10.0", blocker.Dependent.FullName);
        Assert.Contains("Asset-1", blocker.Subject);
    }

    [Fact]
    public async Task RangeRetainingDependent_UsedMemberRemoved_ReturnsElementRemovedWithTheMember()
    {
        var installed = new FakeInstalledModels()
            .Install(GuardModels.Basic("2.4.0"))
            .Install(GuardModels.RangeDependent("Reg-2.10.0", ["Basic@2/Asset-1", "Basic@2/Code-1", "Basic@2/Asset-1.Code"]));

        var result = await Evaluate(installed, GuardModels.Basic("2.5.0", codeOnAsset: false));

        // The removal is also a Major change of the type the dependent derives from: both are reported.
        var blocker = Assert.Single(result.Blockers, b => b.Kind == CkUpgradeBlockerKind.ElementRemoved);
        Assert.Equal("Basic@2/Asset-1.Code", blocker.Subject);
    }

    [Fact]
    public async Task RangeRetainingDependent_UsedElementBecameInternal_ReturnsElementBecameInternal()
    {
        var installed = new FakeInstalledModels()
            .Install(GuardModels.Basic("2.4.0"))
            .Install(GuardModels.RangeDependent("Reg-2.10.0"));

        var result = await Evaluate(installed, GuardModels.Basic("2.5.0", internalAsset: true));

        var blocker = Assert.Single(result.Blockers, b => b.Kind == CkUpgradeBlockerKind.ElementBecameInternal);
        Assert.Contains("Asset-1", blocker.Subject);
        Assert.Equal("CK_UPGRADE_ELEMENT_BECAME_INTERNAL", blocker.Code);
    }

    [Fact]
    public async Task RangeRetainingDependent_UsedElementChangedIncompatibly_ReturnsElementChanged()
    {
        var installed = new FakeInstalledModels()
            .Install(GuardModels.Basic("2.4.0"))
            .Install(GuardModels.RangeDependent("Reg-2.10.0"));

        // The attribute definition Code-1 changes its value type: a Major change that the dependent reuses.
        var result = await Evaluate(installed, GuardModels.Basic("2.5.0", codeType: AttributeValueTypesDto.Integer));

        var blocker = Assert.Single(result.Blockers);
        Assert.Equal(CkUpgradeBlockerKind.ElementChanged, blocker.Kind);
        Assert.Contains("Code-1", blocker.Subject);
    }

    [Fact]
    public async Task RangeRetainingDependent_CandidateBelowFloor_ReturnsFloorNotMetNamingTheRange()
    {
        var installed = new FakeInstalledModels()
            .Install(GuardModels.Basic("2.4.0"))
            .Install(GuardModels.RangeDependent("Reg-2.10.0"));

        var result = await Evaluate(installed, GuardModels.Basic("2.3.0"));

        var blocker = Assert.Single(result.Blockers);
        Assert.Equal(CkUpgradeBlockerKind.FloorNotMet, blocker.Kind);
        Assert.Equal("Basic-[2.4,3.0)", blocker.Subject);
        Assert.Contains("floor", blocker.Message);
    }

    [Fact]
    public async Task RangeRetainingDependent_RangeUpperBoundBelowCandidate_ReturnsRangeNotSatisfied()
    {
        var installed = new FakeInstalledModels()
            .Install(GuardModels.Basic("2.4.0"))
            .Install(GuardModels.RangeDependent("Reg-2.10.0", range: "[2.4,2.6)"));

        var result = await Evaluate(installed, GuardModels.Basic("2.7.0"));

        var blocker = Assert.Single(result.Blockers);
        Assert.Equal(CkUpgradeBlockerKind.RangeNotSatisfied, blocker.Kind);
        Assert.Equal("Basic-[2.4,2.6)", blocker.Subject);
    }

    [Fact]
    public async Task RangeRetainingDependent_NewAttributeCollidesWithDerivedType_ReturnsNameCollision()
    {
        var installed = new FakeInstalledModels()
            .Install(GuardModels.Basic("2.4.0"))
            .Install(GuardModels.RangeDependent("Reg-2.10.0", ownAttribute: "Extra"));

        // Basic 2.5.0 newly declares Extra on Asset-1; Reg-1 (derived from it) already declares Extra.
        var result = await Evaluate(installed, GuardModels.Basic("2.5.0", extraAssetAttribute: "Extra"));

        var blocker = Assert.Single(result.Blockers);
        Assert.Equal(CkUpgradeBlockerKind.NameCollision, blocker.Kind);
        Assert.Equal("CK_UPGRADE_NAME_COLLISION", blocker.Code);
    }

    [Fact]
    public async Task AdditiveUpdate_ForRangeRetainingDependent_IsNotBlocked()
    {
        var installed = new FakeInstalledModels()
            .Install(GuardModels.Basic("2.4.0"))
            .Install(GuardModels.RangeDependent("Reg-2.10.0", ["Basic@2/Asset-1", "Basic@2/Code-1", "Basic@2/Asset-1.Code"]));

        var result = await Evaluate(installed, GuardModels.Basic("2.5.0", extraAssetAttribute: "Fresh"));

        Assert.Empty(result.Blockers);
        Assert.Empty(result.Notes);
    }

    [Fact]
    public async Task MultipleInstalledDependents_AreAllReported_OrderedByDependent()
    {
        var installed = new FakeInstalledModels()
            .Install(GuardModels.Basic("2.4.0"))
            .Install(Plain("Zeta-1.0.0", "Basic-2.4.0"))
            .Install(Plain("Alpha-1.0.0", "Basic-2.4.0"))
            .Install(GuardModels.RangeDependent("Mid-1.0.0"))
            .Install(Plain("Unrelated-1.0.0", "Other-1.0.0"));

        var result = await Evaluate(installed, GuardModels.Basic("2.5.0", withAsset: false));

        Assert.Equal(["Alpha", "Mid", "Zeta"], result.Blockers.Select(b => b.Dependent.Name).Distinct());
        Assert.Equal(CkUpgradeBlockerKind.ExactPin, result.Blockers.First(b => b.Dependent.Name == "Alpha").Kind);
        Assert.Equal(CkUpgradeBlockerKind.ElementRemoved, result.Blockers.First(b => b.Dependent.Name == "Mid").Kind);
        Assert.DoesNotContain(result.Blockers, b => b.Dependent.Name == "Unrelated");
    }

    [Fact]
    public async Task NoInstalledDependents_NoBlocker()
    {
        var onlyBase = new FakeInstalledModels().Install(GuardModels.Basic("2.4.0"));
        Assert.False((await Evaluate(onlyBase, GuardModels.Basic("2.5.0"))).IsBlocked);

        var empty = new FakeInstalledModels();
        Assert.False((await Evaluate(empty, GuardModels.Basic("2.5.0"))).IsBlocked);
        Assert.False((await Evaluate(onlyBase)).IsBlocked);
    }

    [Fact]
    public async Task ResolveFailedInstalledDependent_IsNotReported()
    {
        var installed = new FakeInstalledModels()
            .Install(GuardModels.Basic("2.4.0"))
            .Install(Plain("Broken-1.0.0", "Basic-2.3.0"), ModelState.ResolveFailed)
            .Install(GuardModels.RangeDependent("BrokenRange-1.0.0"), ModelState.ResolveFailed);

        var result = await Evaluate(installed, GuardModels.Basic("2.5.0", withAsset: false));

        Assert.Empty(result.Blockers);
    }

    [Fact]
    public async Task ResolveFailedInstalledCandidateBase_IsNoReplacement()
    {
        // A ResolveFailed Basic cannot be broken further, and the update may repair it.
        var installed = new FakeInstalledModels()
            .Install(GuardModels.Basic("2.4.0"), ModelState.ResolveFailed)
            .Install(Plain("Dep-1.0.0", "Basic-2.4.0"));

        Assert.Empty((await Evaluate(installed, GuardModels.Basic("2.5.0"))).Blockers);
    }

    [Theory]
    [InlineData("System-2.6.0")]
    [InlineData("System.Communication-1.2.0")]
    public async Task ServiceManagedCandidates_AreNeverBlockers(string candidate)
    {
        var name = new CkModelId(candidate).Name;
        var installed = new FakeInstalledModels()
            .Install(Plain(name == "System" ? "System-2.5.0" : "System.Communication-1.1.0"))
            .Install(Plain("System.Bot-1.0.0", "System-2.5.0", "System.Communication-1.1.0"))
            .Install(Plain("Dep-1.0.0", "System-2.5.0"));

        var result = await Evaluate(installed, Plain(candidate));

        Assert.Empty(result.Blockers);
    }

    private static Task<CkUpgradeGuardResult> EvaluateExplicit(FakeInstalledModels installed,
        params CkCompiledModelRoot[] updateSet) =>
        GuardModels.Guard().EvaluateAsync(installed, updateSet, null, CancellationToken.None, true);

    /// <summary>The repro of AB#6966: System-2.8.0 drops the attribute a System.Identity-like dependent uses.</summary>
    private static FakeInstalledModels SystemTenant() => new FakeInstalledModels()
        .Install(GuardModels.Basic("2.5.0", name: "System"))
        .Install(GuardModels.RangeDependent("System.Identity-2.26.0", baseName: "System", range: "[2.5,3.0)",
            floor: "2.5.0"));

    [Fact]
    public async Task ExplicitCaller_BreakingSystemCandidate_IsJudgedAndBlocks()
    {
        var result = await EvaluateExplicit(SystemTenant(), GuardModels.Basic("2.8.0", name: "System", withAsset: false));

        Assert.True(result.IsBlocked);
        var blocker = Assert.Single(result.Blockers);
        Assert.Equal(CkUpgradeBlockerKind.ElementRemoved, blocker.Kind);
        Assert.Equal("System.Identity-2.26.0", blocker.Dependent.FullName);
        Assert.Equal("System-2.8.0", blocker.Update.FullName);
    }

    [Fact]
    public async Task DefaultCaller_BreakingSystemCandidate_IsStillSkipped()
    {
        // Pins that the service-driven paths (blueprint apply/update, FixAll, batch import) keep skipping System.
        var result = await Evaluate(SystemTenant(), GuardModels.Basic("2.8.0", name: "System", withAsset: false));

        Assert.Empty(result.Blockers);
    }

    [Fact]
    public async Task ExplicitCaller_AdditiveSystemCandidate_IsNotBlocked()
    {
        var result = await EvaluateExplicit(SystemTenant(),
            GuardModels.Basic("2.8.0", name: "System", extraAssetAttribute: "Extra"));

        Assert.Empty(result.Blockers);
    }

    [Fact]
    public async Task ExplicitCaller_HarmlessSystemDowngrade_IsNotBlocked_BreakingOneIs()
    {
        var installed = new FakeInstalledModels()
            .Install(GuardModels.Basic("2.8.0", name: "System"))
            .Install(GuardModels.RangeDependent("System.Identity-2.26.0", baseName: "System", range: "[2.5,3.0)",
                floor: "2.5.0"));

        Assert.Empty((await EvaluateExplicit(installed, GuardModels.Basic("2.5.0", name: "System"))).Blockers);

        var breaking = await EvaluateExplicit(installed, GuardModels.Basic("2.5.0", name: "System", withAsset: false));
        Assert.Equal(CkUpgradeBlockerKind.ElementRemoved, Assert.Single(breaking.Blockers).Kind);
    }

    [Fact]
    public async Task ExplicitCaller_FirstInstallAndSameVersionAndOtherMajorSystem_AreNoReplacement()
    {
        var empty = new FakeInstalledModels();
        Assert.Empty((await EvaluateExplicit(empty, GuardModels.Basic("2.8.0", name: "System"))).Blockers);

        var tenant = SystemTenant();
        Assert.Empty((await EvaluateExplicit(tenant, GuardModels.Basic("2.5.0", name: "System", withAsset: false))).Blockers);
        Assert.Empty((await EvaluateExplicit(tenant, GuardModels.Basic("3.0.0", name: "System", withAsset: false))).Blockers);
    }

    [Fact]
    public async Task CandidateOfAnotherMajor_IsAParallelInstall_NotABlocker()
    {
        var installed = new FakeInstalledModels()
            .Install(GuardModels.Basic("2.4.0"))
            .Install(Plain("Dep-1.0.0", "Basic-2.4.0"))
            .Install(GuardModels.RangeDependent("Reg-2.10.0"));

        var result = await Evaluate(installed, Plain("Basic-3.0.0"));

        Assert.Empty(result.Blockers);
    }

    [Fact]
    public async Task CandidateWithTheInstalledVersion_ChangesNothing()
    {
        var installed = new FakeInstalledModels()
            .Install(Plain("Basic-2.4.0"))
            .Install(Plain("Dep-1.0.0", "Basic-2.4.0"));

        Assert.Empty((await Evaluate(installed, Plain("Basic-2.4.0"))).Blockers);
    }

    [Fact]
    public async Task ExactPinOnAnotherMajor_IsNotAffected()
    {
        var installed = new FakeInstalledModels()
            .Install(Plain("Basic-2.4.0"))
            .Install(Plain("OldDep-1.0.0", "Basic-1.9.0"));

        Assert.Empty((await Evaluate(installed, Plain("Basic-2.5.0"))).Blockers);
    }

    [Fact]
    public async Task LegacyRangeRetainingDependentWithoutUsedSurface_IsCheckedByReferences_AndSaysSo()
    {
        var legacy = GuardModels.RangeDependent("Legacy-1.0.0");
        legacy.DependencyRanges![0].UsedSurface = null;
        legacy.DependencyRanges[0].UsedSurfaceHash = null;
        var installed = new FakeInstalledModels().Install(GuardModels.Basic("2.4.0")).Install(legacy);

        // Defined verdict: judged on the elements the model references; members it uses are unknown -> a note.
        var removed = await Evaluate(installed, GuardModels.Basic("2.5.0", withAsset: false));
        Assert.Equal(CkUpgradeBlockerKind.ElementRemoved, Assert.Single(removed.Blockers).Kind);
        Assert.Contains(removed.Notes, n => n.Contains("no usedSurface", StringComparison.Ordinal) && n.Contains("Legacy-1.0.0"));

        var additive = await Evaluate(installed, GuardModels.Basic("2.5.0", extraAssetAttribute: "Fresh"));
        Assert.Empty(additive.Blockers);
        Assert.Single(additive.Notes);
    }

    [Fact]
    public async Task LegacyClassicDependentWithoutRanges_IsJudgedByItsExactPins()
    {
        // A document written before range retention and usedSurface: Dependencies only, no CkLanguage.
        var installed = new FakeInstalledModels()
            .Install(Plain("Basic-2.4.0"))
            .Install(Plain("Old-0.9.0", "Basic-2.4.0"));

        var result = await Evaluate(installed, Plain("Basic-2.5.0"));

        Assert.Equal(CkUpgradeBlockerKind.ExactPin, Assert.Single(result.Blockers).Kind);
    }

    [Fact]
    public async Task DependentThatCannotBeLoaded_IsNotAssumedHealthy_ItIsAnnounced()
    {
        var installed = new FakeInstalledModels()
            .Install(GuardModels.Basic("2.4.0"))
            .Install(GuardModels.RangeDependent("Reg-2.10.0"), loadable: false);

        var result = await Evaluate(installed, GuardModels.Basic("2.5.0"));

        Assert.Empty(result.Blockers);
        Assert.Contains(result.Notes, n => n.Contains("Reg-2.10.0 could not be loaded", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Evaluation_IsReadOnly_AndLoadsOnlyWhatItNeeds()
    {
        var installed = new FakeInstalledModels()
            .Install(GuardModels.Basic("2.4.0"))
            .Install(Plain("Exact-1.0.0", "Basic-2.4.0"))
            .Install(GuardModels.RangeDependent("Reg-2.10.0"));

        await Evaluate(installed, GuardModels.Basic("2.5.0"));

        Assert.Equal(1, installed.ListCalls);
        // The baseline and the one range-retaining dependent; the exact-pinned one is judged from its row.
        Assert.Equal(2, installed.LoadCalls);

        // The reader the guard works through has no write member: reads only.
        var members = typeof(IInstalledCkModelReader).GetMethods().Select(m => m.Name).OrderBy(n => n);
        Assert.Equal(["ListInstalledAsync", "LoadAsync"], members);
    }

    [Fact]
    public async Task HundredInstalledModelsWithTenUpdates_CompleteWithinTwoSeconds()
    {
        var installed = new FakeInstalledModels();
        var updates = new List<CkCompiledModelRoot>();
        for (var i = 0; i < 10; i++)
        {
            installed.Install(Plain($"Lib{i}-1.0.0"));
            updates.Add(Plain($"Lib{i}-1.1.0"));
        }

        for (var i = 0; i < 90; i++)
        {
            // Mix: exact-pinned dependents (broken by the update) and range-retaining ones on Basic.
            installed.Install(Plain($"App{i}-1.0.0", $"Lib{i % 10}-1.0.0"));
        }

        var stopwatch = Stopwatch.StartNew();
        var result = await Evaluate(installed, updates.ToArray());
        stopwatch.Stop();

        Assert.Equal(90, result.Blockers.Count);
        Assert.True(stopwatch.Elapsed < TimeSpan.FromSeconds(2), $"took {stopwatch.Elapsed}");
        Assert.Equal(0, installed.LoadCalls);
    }

    [Theory]
    [InlineData("floor not met: X requires Basic >= 2.4.0 (range [2.4,3.0)), the candidate is 2.3.0", CkUpgradeBlockerKind.FloorNotMet)]
    [InlineData("X uses Basic@2/Asset-1, which Basic-2.5.0 does not define (type removed or re-identified)", CkUpgradeBlockerKind.ElementRemoved)]
    [InlineData("X uses Basic@2/Asset-1, which is internal in Basic-2.5.0", CkUpgradeBlockerKind.ElementBecameInternal)]
    [InlineData("X uses Basic@2/Code-1, which changes incompatibly: modified (removed internal)", CkUpgradeBlockerKind.ElementChanged)]
    [InlineData("X uses Basic@2/Asset-1.Code: the attribute 'Code' is no longer declared by Basic-2.5.0/Asset-1 or its bases", CkUpgradeBlockerKind.ElementRemoved)]
    [InlineData("Basic-2.5.0/Asset-1 adds the attribute 'Extra', which the derived type Reg-1.0.0/Reg-1 already declares (name collision, error 13)", CkUpgradeBlockerKind.NameCollision)]
    [InlineData("Basic-2.5.0/Asset-1 adds the method 'M', which the derived type Reg-1.0.0/Reg-1 already declares (duplicate method id, error 100)", CkUpgradeBlockerKind.NameCollision)]
    public void CheckerFindings_AreMappedToBlockerKinds(string reason, CkUpgradeBlockerKind expected) =>
        Assert.Equal(expected, TenantUpgradeGuard.Classify(reason));

    [Theory]
    [InlineData("X uses Basic@2/Asset-1, which is internal in Basic-2.5.0", "Basic@2/Asset-1")]
    [InlineData("X uses Basic@2/Asset-1.Code: the attribute 'Code' is no longer declared", "Basic@2/Asset-1.Code")]
    [InlineData("Basic-2.5.0/Asset-1 adds the attribute 'Extra', which", "Basic-2.5.0/Asset-1")]
    public void SubjectOfAFinding_IsTheElementOrMember(string reason, string expected) =>
        Assert.Equal(expected, TenantUpgradeGuard.Subject(reason));
}
