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
    ///     without and 6.7 GiB with <see cref="MongoFileHandleParameters" />. Most of it is not data: every fixture keeps
    ///     its databases in the shared server (130,000-140,000 WiredTiger data handles per run, up to ~1,300
    ///     connections). A 3 GiB limit was OOM-killed in the middle of the run. 12 GiB leaves headroom over the highest
    ///     peak; when the suite outgrows it the container is OOM-killed and the tests fail with "Connection refused" -
    ///     raise the limit with a new measurement, or let fixtures drop their databases.
    /// </summary>
    public const long MongoMemoryLimitBytes = 12 * GiB;

    /// <summary>
    ///     CrateDB JVM heap (<c>CRATE_HEAP_SIZE</c>).
    /// </summary>
    public const string CrateHeapSize = "1g";

    /// <summary>
    ///     Container memory limit of a CrateDB test container: the 1 GB heap plus off-heap memory and Lucene's file
    ///     cache. Measured 2026-11-07: peak 1.9 GiB (staging copy fixture) and 1.1 GiB (blueprint archive fixture).
    /// </summary>
    public const long CrateMemoryLimitBytes = 3 * GiB;

    /// <summary>
    ///     Returns a create-parameter modifier that sets a hard memory limit without swap.
    /// </summary>
    public static Action<CreateContainerParameters> MemoryLimit(long bytes)
    {
        return parameters =>
        {
            parameters.HostConfig ??= new HostConfig();
            parameters.HostConfig.Memory = bytes;
            // Same value as Memory: no swap on top, so the limit is the real ceiling.
            parameters.HostConfig.MemorySwap = bytes;
        };
    }
}
