using System.Globalization;
using Docker.DotNet.Models;
using Meshmakers.Octo.Runtime.Engine.MongoDb.IntegrationTests.Fixtures;
using Meshmakers.Octo.Runtime.Engine.MongoDb.IntegrationTests.StagingCopy;
using Xunit;

namespace Meshmakers.Octo.Runtime.Engine.MongoDb.IntegrationTests;

/// <summary>
///     AB#9250: a CrateDB container is asked what it got - the JVM heap and the processors the JVM sees under the CPU
///     quota - so the test fails if Testcontainers drops the create-parameter modifier.
/// </summary>
[Collection(CrateStagingCopyCollection.Name)]
public class CrateTestContainerLimitsTests(CrateStagingCopyFixture fixture)
{
    private const long GiB = 1024L * 1024 * 1024;

    [Fact]
    public async Task CrateContainer_RunsWithItsHeap_AndSeesTheCpuLimitAsItsProcessors()
    {
        var node = (await fixture.QueryAsync(
            "SELECT heap['max'] AS heap_max, os_info['available_processors'] AS processors FROM sys.nodes")).Single();

        var heapMax = Convert.ToInt64(node["heap_max"], CultureInfo.InvariantCulture);
        // CRATE_HEAP_SIZE=1g; the JVM reports its maximum heap slightly below -Xmx
        Assert.InRange(heapMax, 900L * 1024 * 1024, GiB);
        // the JVM derives its processor count from the CFS quota that NanoCpus sets (or from the host, when the host has
        // fewer CPUs); without the quota it would see every CPU of the Docker VM
        Assert.InRange(Convert.ToInt32(node["processors"], CultureInfo.InvariantCulture), 1,
            (int)Math.Ceiling(TestContainerLimits.CrateCpuLimit));
    }
}

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
