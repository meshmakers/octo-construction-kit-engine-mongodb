using Docker.DotNet.Models;
using Meshmakers.Octo.Runtime.Engine.MongoDb.IntegrationTests.Fixtures;
using Xunit;

namespace Meshmakers.Octo.Runtime.Engine.MongoDb.IntegrationTests;

/// <summary>
///     AB#9250: every Testcontainer of the suite gets a memory and a CPU limit from one modifier, and the limits of one run
///     fit a developer workstation. No container: these tests run without Docker.
/// </summary>
public class TestContainerLimitsValueTests
{
    private const long GiB = 1024L * 1024 * 1024;

    [Fact]
    public void Resources_SetsAHardMemoryLimitWithoutSwap_AndACpuQuota()
    {
        var parameters = new CreateContainerParameters();

        TestContainerLimits.Resources(3 * GiB, 1.5)(parameters);

        var hostConfig = Assert.IsType<HostConfig>(parameters.HostConfig);
        Assert.Equal(3 * GiB, hostConfig.Memory);
        Assert.Equal(3 * GiB, hostConfig.MemorySwap);
        Assert.Equal(1_500_000_000L, hostConfig.NanoCPUs);
    }

    [Fact]
    public void Resources_RefusesLimitsThatWouldMeanNoLimit()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => TestContainerLimits.Resources(0, 2));
        Assert.Throws<ArgumentOutOfRangeException>(() => TestContainerLimits.Resources(GiB, 0));
        // positive, but below Docker's 0.01 CPUs it would round to NanoCPUs = 0, which means no limit
        Assert.Throws<ArgumentOutOfRangeException>(() => TestContainerLimits.Resources(GiB, 0.0000000005));
        Assert.Throws<ArgumentOutOfRangeException>(() => TestContainerLimits.Resources(GiB, 0.009));
        TestContainerLimits.Resources(GiB, 0.01)(new CreateContainerParameters());
    }

    [Fact]
    public void TheLimitsOfOneRun_FitTheDockerVmOfA32GiBWorkstation()
    {
        // the CI suite (Category!=Stress): the shared mongod and the two CrateDB containers of the CI collections
        Assert.True(TestContainerLimits.CiSuiteMemoryBytes <= TestContainerLimits.RecommendedDockerVmMemoryBytes,
            $"{TestContainerLimits.CiSuiteMemoryBytes / GiB} GiB of limits do not fit the recommended Docker VM");
        // everything at once, the stress container included, still leaves the host a quarter of 32 GiB
        Assert.True(TestContainerLimits.WorstCaseMemoryBytes <= 24 * GiB,
            $"worst case {TestContainerLimits.WorstCaseMemoryBytes / GiB} GiB");
        Assert.True(TestContainerLimits.RecommendedDockerVmMemoryBytes <= 24 * GiB);
        Assert.True(TestContainerLimits.WorstCaseCpus <= TestContainerLimits.RecommendedDockerVmCpus,
            $"worst case {TestContainerLimits.WorstCaseCpus} CPUs");
    }
}
