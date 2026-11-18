using Docker.DotNet.Models;

namespace Meshmakers.Octo.Runtime.Engine.MongoDb.IntegrationTests.Fixtures;

/// <summary>
///     Memory caps for the Testcontainers this suite starts (AB#8511).
/// </summary>
/// <remarks>
///     <para>
///         Without a cap a container is sized from the Docker host. On the CI node (one 125 GiB host, every
///         agent's Docker-in-Docker containers in one shared cgroup) mongod then sized its WiredTiger cache
///         to ~62 GB and grew with the suite; when several agents ran Testcontainers suites at once the
///         shared cgroup hit its <c>memory.high</c> and the kernel throttled all of them together. Tests
///         stalled for minutes, MongoDB aborted transactions after its 60 s lifetime limit and CrateDB DDL
///         ran past its 30 s budget.
///     </para>
///     <para>
///         The caps keep one suite inside a fixed, small footprint. They are not a reason to raise
///         <c>transactionLifetimeLimitSeconds</c> or any test budget: a test that needs more is a finding,
///         not something to hide.
///     </para>
/// </remarks>
internal static class TestContainerLimits
{
    private const long GiB = 1024L * 1024 * 1024;

    /// <summary>
    ///     WiredTiger cache of the shared mongod, in GB (<c>--wiredTigerCacheSizeGB</c>). Without the flag mongod takes
    ///     half of the memory it sees (about 62 GB on the CI node).
    /// </summary>
    /// <remarks>
    ///     3 GB since 2026-11-16 (AB#8511 reopened). 1 GB was too small: the suite's data is small, but its tens of
    ///     thousands of collections and indexes (every fixture keeps its databases) occupy cache pages too, and when the
    ///     parallel fixtures import their CK models and create their system tenants at the start of the run, WiredTiger
    ///     rolled transactions back under cache pressure (<c>transaction rolled back because of cache overflow</c>,
    ///     CI builds 70416 and 70685: the system tenant was then missing and up to 36 tests failed). The cache counts
    ///     against <see cref="MongoMemoryLimitBytes" />; keep the sum of cache and the measured non-cache footprint
    ///     below that limit.
    /// </remarks>
    public const string MongoWiredTigerCacheSizeGb = "3";

    /// <summary>
    ///     Server parameters that make the shared mongod close idle WiredTiger data handles early (AB#8511, 2026-11-16).
    /// </summary>
    /// <remarks>
    ///     Every fixture keeps its databases in the shared server, so a full run opens 130,000-140,000 data handles
    ///     (one per collection and index). An open handle costs memory outside the cache and pins pages in the cache;
    ///     mongod closes a handle only after it was idle for <c>wiredTigerFileHandleCloseIdleTime</c> (default 600 s),
    ///     and only while more than <c>wiredTigerFileHandleCloseMinimum</c> (default 250) are open. The databases of a
    ///     fixture whose tests are done stay idle for the rest of the run, so closing after 30 s returns that memory
    ///     long before the default would. A handle that is used again is simply reopened.
    /// </remarks>
    public static readonly string[] MongoFileHandleParameters =
    [
        "--setParameter", "wiredTigerFileHandleCloseIdleTime=30",
        "--setParameter", "wiredTigerFileHandleCloseScanInterval=10",
        "--setParameter", "wiredTigerFileHandleCloseMinimum=250",
    ];

    /// <summary>
    ///     Container memory limit of the shared mongod. Measured with cgroup <c>memory.peak</c> on full runs:
    ///     2026-11-07 (1,082 tests, cache 1 GB) 7.1, 6.4 and 8.4 GiB; 2026-11-16 (1,454 tests, cache 3 GB) 9.1 GiB
    ///     without and 6.7 GiB with <see cref="MongoFileHandleParameters" />; 2026-11-18 (local Windows, Docker Desktop,
    ///     AB#9250) 7.9 GiB. Most of it is not data: every fixture keeps
    ///     its databases in the shared server (130,000-140,000 WiredTiger data handles per run, up to ~1,300
    ///     connections). A 3 GiB limit was OOM-killed in the middle of the run. 12 GiB leaves headroom over the highest
    ///     peak; when the suite outgrows it the container is OOM-killed and the tests fail with "Connection refused" -
    ///     raise the limit with a new measurement, or let fixtures drop their databases.
    /// </summary>
    public const long MongoMemoryLimitBytes = 12 * GiB;

    /// <summary>
    ///     CrateDB JVM heap (<c>CRATE_HEAP_SIZE</c>) of the CrateDB containers of the CI suite
    ///     (<c>BlueprintArchiveActivationFixture</c>, <c>CrateStagingCopyFixture</c>).
    /// </summary>
    public const string CrateHeapSize = "1g";

    /// <summary>
    ///     CrateDB JVM heap of the stress container (<c>CrateMemoryPressureFixture</c>, <c>Category=Stress</c>, AB#8679): small on
    ///     purpose, so that the query circuit breaker the tests set is a few dozen MB and parallel writes trip it.
    /// </summary>
    public const string CrateStressHeapSize = "512m";

    /// <summary>
    ///     Container memory limit of a CrateDB test container: the 1 GB heap plus off-heap memory and Lucene's file
    ///     cache. Measured 2026-11-07: peak 1.9 GiB (staging copy fixture) and 1.1 GiB (blueprint archive fixture).
    /// </summary>
    public const long CrateMemoryLimitBytes = 3 * GiB;

    /// <summary>
    ///     CPU limit of the shared mongod, in CPUs (Docker <c>NanoCpus</c>; AB#9250). Without a limit a container may use
    ///     every core of the Docker host, so one suite could starve the rest of the machine (other lanes, a kind cluster).
    ///     Measured 2026-11-18 on a full local run without the limit (22 logical CPUs, cgroup <c>cpu.stat</c> sampled
    ///     every ~8 s): average 0.62, p95 1.19, highest sample 1.88 CPUs. 4 leaves more than twice the highest sample, so
    ///     the limit only takes effect when something goes wrong. Docker refuses a CPU limit above the number of CPUs of its
    ///     daemon, so the suite needs a Docker daemon (VM) with at least 4 CPUs (CI agents: 8; README: 12 recommended).
    /// </summary>
    public const double MongoCpuLimit = 4;

    /// <summary>
    ///     CPU limit of every CrateDB test container, in CPUs (Docker <c>NanoCpus</c>; AB#9250). The JVM sizes its thread
    ///     pools from the processors it sees, which follow the quota (2). Measured 2026-11-18 without the limit: staging copy
    ///     fixture average 0.72, p95 1.71, highest sample 2.42 CPUs (memory peak 2.09 GiB); blueprint archive fixture highest
    ///     sample 1.14 CPUs (1.28 GiB). Short bursts above 2 are throttled; with the limit the staging copy collection took 8:19 min (8:43 without), its
    ///     highest sample 1.60 CPUs.
    /// </summary>
    public const double CrateCpuLimit = 2;

    /// <summary>
    ///     Number of CrateDB containers a run can hold at once: one per CrateDB collection fixture
    ///     (<c>BlueprintArchiveActivationFixture</c>, <c>CrateStagingCopyFixture</c>, <c>CrateMemoryPressureFixture</c>). Each
    ///     fixture belongs to exactly one xUnit collection, so it starts one container, and the tests that share it run one
    ///     after the other.
    /// </summary>
    public const int MaxCrateContainers = 3;

    /// <summary>
    ///     Upper bound of the memory all containers of one run can take together (the shared mongod plus every CrateDB
    ///     container at its limit): 21 GiB. The limits are ceilings, not reservations; the measured peaks add up to far less
    ///     (see the limits above). It has to fit the Docker Desktop VM of a developer workstation with 32 GiB RAM.
    /// </summary>
    public const long WorstCaseMemoryBytes = MongoMemoryLimitBytes + MaxCrateContainers * CrateMemoryLimitBytes;

    /// <summary>
    ///     Upper bound of the memory of the CI suite (<c>Category!=Stress</c>; the stress container is never started): the
    ///     shared mongod and the two CrateDB containers of the CI collections.
    /// </summary>
    public const long CiSuiteMemoryBytes = MongoMemoryLimitBytes + (MaxCrateContainers - 1) * CrateMemoryLimitBytes;

    /// <summary>Upper bound of the CPUs all containers of one run can take together.</summary>
    public const double WorstCaseCpus = MongoCpuLimit + MaxCrateContainers * CrateCpuLimit;

    /// <summary>
    ///     Memory the Docker Desktop VM (WSL 2) should have for a local run: the limits of the CI suite plus room for the
    ///     Docker engine itself. Docker Desktop gives the VM half of the host's RAM by default (16 GiB on a 32 GiB machine),
    ///     which is below the sum of the limits; set it in <c>%UserProfile%\.wslconfig</c> (<c>[wsl2] memory=20GB</c>).
    /// </summary>
    public const long RecommendedDockerVmMemoryBytes = 20 * GiB;

    /// <summary>CPUs the Docker VM should have, so that every container can reach its CPU limit at the same time.</summary>
    public const double RecommendedDockerVmCpus = 12;

    /// <summary>
    ///     Returns a create-parameter modifier that sets a hard memory limit without swap and a CPU limit.
    /// </summary>
    /// <param name="memoryBytes">Container memory limit (also the memory + swap limit, so no swap on top).</param>
    /// <param name="cpus">CPU limit in CPUs, e.g. 2 or 1.5 (Docker <c>--cpus</c>).</param>
    public static Action<CreateContainerParameters> Resources(long memoryBytes, double cpus)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(memoryBytes);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(cpus);
        // Docker accepts 0.01 CPUs at least; a smaller value would round to NanoCPUs = 0, which means "no limit"
        var nanoCpus = (long)(cpus * 1_000_000_000);
        ArgumentOutOfRangeException.ThrowIfLessThan(nanoCpus, 10_000_000L, nameof(cpus));
        return parameters =>
        {
            parameters.HostConfig ??= new HostConfig();
            parameters.HostConfig.Memory = memoryBytes;
            // Same value as Memory: no swap on top, so the limit is the real ceiling.
            parameters.HostConfig.MemorySwap = memoryBytes;
            // CFS quota, the same as `docker run --cpus`: the container may use that many CPUs' worth of time and is
            // throttled above it, so one container cannot take every core of the host.
            parameters.HostConfig.NanoCPUs = nanoCpus;
        };
    }
}
