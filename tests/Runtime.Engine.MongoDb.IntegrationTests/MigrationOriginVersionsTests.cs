using Meshmakers.Octo.ConstructionKit.Contracts;
using Meshmakers.Octo.ConstructionKit.Contracts.DataTransferObjects;

using Xunit;

namespace Meshmakers.Octo.Runtime.Engine.MongoDb.IntegrationTests;

/// <summary>AB#6720: which stored version of a CK model a data migration starts from.</summary>
public class MigrationOriginVersionsTests
{
    private static (string, CkVersion, ModelState) M(string name, string version, ModelState state) =>
        (name, new CkVersion(version), state);

    [Fact]
    public void ResolveFailedVersion_IsTheOrigin_WhenNothingIsAvailable()
    {
        var origins = MigrationOriginVersions.Select([M("System.Communication", "3.41.0", ModelState.ResolveFailed)]);

        Assert.Equal([("System.Communication", new CkVersion("3.41.0"))], origins);
    }

    [Fact]
    public void AvailableVersion_WinsOverResolveFailed_EvenWhenLower()
    {
        var origins = MigrationOriginVersions.Select(
        [
            M("Test", "2.0.0", ModelState.ResolveFailed),
            M("Test", "1.0.0", ModelState.Available)
        ]);

        Assert.Equal([("Test", new CkVersion("1.0.0"))], origins);
    }

    [Fact]
    public void HighestVersionOfTheSameState_Wins()
    {
        var origins = MigrationOriginVersions.Select(
        [
            M("Test", "3.9.0", ModelState.ResolveFailed),
            M("Test", "3.41.0", ModelState.ResolveFailed),
            M("Test", "3.10.0", ModelState.ResolveFailed)
        ]);

        Assert.Equal([("Test", new CkVersion("3.41.0"))], origins);
    }

    [Fact]
    public void ImportingModels_AreNeverAnOrigin_AndModelsAreIndependent()
    {
        var origins = MigrationOriginVersions.Select(
        [
            M("Importing", "1.0.0", ModelState.Importing),
            M("A", "1.0.0", ModelState.Available),
            M("B", "2.0.0", ModelState.ResolveFailed)
        ]);

        Assert.Equal(2, origins.Count);
        Assert.Contains(("A", new CkVersion("1.0.0")), origins);
        Assert.Contains(("B", new CkVersion("2.0.0")), origins);
    }
}
