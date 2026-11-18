# Runtime.Engine.MongoDb.IntegrationTests

Integration tests of the MongoDB runtime engine against a Testcontainers MongoDB replica set (and, for the stream data
tests, a Testcontainers CrateDB). Layout, parallelism rules and the CI time budget: repository `CLAUDE.md`, section
*Test layout, parallelism rules and the CI time budget*.

## Container resources and parallelism (AB#9250)

Every Testcontainer of the suite has a memory **and** a CPU limit, and xUnit runs at most 8 collections at once, so a
full run takes a bounded share of the machine and stays predictable next to other work (other agent lanes, Docker
builds, a local kind cluster). Values in `Fixtures/TestContainerLimits.cs` (with the measurements behind them); all four
fixtures apply them through one modifier, `TestContainerLimits.Resources(memory, cpus)`. Pinned by
`TestContainerLimitsValueTests` (the modifier and the sums, no Docker), `TestContainerLimitsTests` (the cgroup limits
mongod runs under) and `CrateTestContainerLimitsTests` (heap and processors the staging copy CrateDB sees):

| Container | Fixture (collection) | Memory limit | CPU limit | JVM heap / cache |
|---|---|---|---|---|
| shared mongod | `SharedMongoDbContainer` (all MongoDB collections) | 12 GiB | 4 CPUs | WiredTiger cache 3 GB |
| CrateDB | `CrateStagingCopyFixture` (`CrateStagingCopy`, ~40 classes) | 3 GiB | 2 CPUs | heap 1 GB |
| CrateDB | `BlueprintArchiveActivationFixture` (`BlueprintArchiveActivation`) | 3 GiB | 2 CPUs | heap 1 GB |
| CrateDB, stress only | `CrateMemoryPressureFixture` (`CrateMemoryPressure`, `Category=Stress`) | 3 GiB | 2 CPUs | heap 512 MB |

Measured on full local runs on 2026-11-18 (Windows 11, 22 logical CPUs, 63 GiB RAM, Docker Desktop VM 31 GiB / 22 CPUs,
a kind cluster running next to it), cgroup `memory.peak` and `cpu.stat` of each container sampled every ~8 s:

| Container | Memory peak | CPUs without limits (avg / p95 / max) | CPUs with limits (avg / p95 / max) |
|---|---|---|---|
| shared mongod | 7.9 GiB | 0.62 / 1.19 / 1.88 | 0.58 / 1.09 / 1.75 |
| CrateDB staging copy | 2.1 GiB | 0.72 / 1.71 / 2.42 | 0.78 / 1.40 / 1.60 |
| CrateDB blueprint archive | 1.3 GiB | 0.27 / 1.14 / 1.14 | not sampled |

`memory.peak` counts the page cache of the container too; `docker stats` showed about 3.6 GiB for mongod at the same time.
With the limits (and `maxParallelThreads: 8`) the run had the same shape as without them: 32 of 33 collections done after
13:00 min (13:30 without), the staging copy collection took 8:19 min (8:43 without), and the suite ended normally after
20:41 min (filtered run, 1,531 passed, 0 failed). The tail of a local Windows run is `ImportTestCkModelCollection` (30 test
classes in one collection, among them `TiebreakIndexUpgradeTests` with 60,000 entities); mongod used about 0.2 CPUs there,
so that part waits on round trips through the Docker Desktop port forwarding, not on CPU. The CI (Linux) runs the whole
suite in about 6 minutes.

- **Stress tests under the CPU limit.** The stress container has the same 2-CPU limit. On the local Windows machine the
  guarded write test (`ArchiveWriteUnderMemoryPressureIntegrationTests.GuardedWrites_StoreWhatTheyReport_OrFailLoudly`) was
  skipped with "The CrateDB memory breaker never tripped" in most runs, with 2, 4 and 8 CPUs alike, and passed in the one
  run without a limit; the other three stress tests tripped the breaker and passed every time. A skip proves nothing and
  fails nothing; the nightly Linux run (`octo-construction-kit-engine-mongodb-Performance`) tripped the breaker in all four
  tests (runs 79174, 71153, 70255).
- **One container per CrateDB fixture.** Each CrateDB fixture belongs to exactly one xUnit collection, so it starts one
  container, and the tests that share it run one after the other. At most three CrateDB containers exist at once (two
  without the stress tests).
- **Sum of the limits:** the CI suite (`Category!=Stress`) 18 GiB / 8 CPUs, everything at once 21 GiB / 10 CPUs. The limits
  are ceilings, not reservations; the measured peaks add up to far less (table above).
- **`maxParallelThreads: 8`** (`xunit.runner.json`). Without it xUnit runs as many collections at once as the machine has
  logical CPUs: 22 on a current developer laptop, where every collection fixture then creates its system tenant and imports
  its CK models at the same moment against the one mongod. 8 is what the CI agents ran with before (their log shows 8
  fixtures initialising at once), so the CI time does not change, and it is at least the number of collections that use
  a CrateDB container (2, 3 with the stress tests) plus the long MongoDB chains.

### Running the suite locally on Windows

1. **Docker Desktop memory.** Docker Desktop (WSL 2) gives its VM half of the host's RAM by default - 16 GiB on a 32 GiB
   machine, less than the 18 GiB of limits of the CI suite. Give it at least **20 GiB** and **12 CPUs** in
   `%UserProfile%\.wslconfig`, then `wsl --shutdown` and restart Docker Desktop:

   ```ini
   [wsl2]
   memory=20GB
   processors=12
   ```

   Check with `docker info --format "{{.NCPU}} CPUs, {{.MemTotal}} bytes"`. A local kind cluster or other containers in the
   same VM need their share on top.
2. **One heavy Docker job at a time.** Do not run two integration suites, or a suite and Docker image builds, at the same
   time on one workstation: on 2026-11-17 a parallel image build turned a 6-minute slice into 50 minutes with CrateDB connect
   timeouts. Several agent lanes on one machine serialize their heavy Docker runs.
3. **Build, then run** (Git Bash or PowerShell, from the repository root):

   ```bash
   dotnet build Octo.ConstructionKit.Engine.MongoDb.sln -c DebugL
   dotnet test tests/Runtime.Engine.MongoDb.IntegrationTests -c DebugL --no-build --filter "Category!=LoadSensitive"
   ```

   Known local Windows problems are filtered with `FullyQualifiedName!~<test>` while their Bugs are open (AB#9145, AB#9105,
   AB#8555, AB#9203; the PR of a change names the filter it used).
4. **A CrateDB container that does not answer** fails `DownsamplingPhaseProbePoolIntegrationTests` and
   `CrateConnectionPoolReturnTests` with a message naming the container, its port and the connect timeout (they wait up to
   30 s for it before they measure), not with a bare `BrokenCircuitException`. That points at the machine (Docker VM
   memory, other Docker work), not at the code.
5. **Slow connection setup on Windows.** A new connection through the Docker Desktop port proxy sometimes takes several
   seconds while CrateDB is idle (2026-11-18: in one of the AC4 runs the `CrateStagingCopy` collection took about four times
   as long as in the run before, CrateDB at ~0.06 CPUs, and the ten-parallel probe test failed on a 3 s Npgsql connect
   timeout, although direct TCP connects to the same port took 0-46 ms; many connects at once each took longer than 3 s,
   single ones did not). The two pool test classes therefore open the connections of their pool once, one after the other,
   and give them back before the measured part (`CrateTestConnect.WarmUpAsync`, same data source as the test, as many
   connections as the test holds at once), so the 3 s pool wait they prove is not spent on connects. A failed warm-up names the container
   and the slow connection setup. The cause is tracked in a follow-up Bug.

## Container memory caps (AB#8511)

Every Testcontainer of this suite has a fixed memory footprint (`Fixtures/TestContainerLimits.cs`):

| Container | Setting | Container memory limit |
|---|---|---|
| shared mongod (`SharedMongoDbContainer`) | `--wiredTigerCacheSizeGB 3`, idle data handles closed after 30 s | 12 GiB, no swap (measured peak 6.7 GiB) |
| CrateDB (`BlueprintArchiveActivationFixture`, `CrateStagingCopyFixture`) | `CRATE_HEAP_SIZE=1g` | 3 GiB, no swap (measured peak 1.9 GiB) |

Without them mongod sized its WiredTiger cache from the Docker host (125 GiB on the CI node). With several suites on
one node, the agents' shared Docker cgroup was throttled as a whole: tests stalled for minutes, MongoDB aborted
transactions after its 60 s `transactionLifetimeLimitSeconds` and CrateDB DDL ran past its 30 s budget. Do not raise
the transaction lifetime or a test budget to get around a stall; a suite that needs more memory than the caps is a
finding. `TestContainerLimitsTests` asks the running mongod for its cache size and its memory limit.

The mongod memory is mostly not data: every fixture keeps its databases in the shared server, so a full run opens
130,000-140,000 WiredTiger data handles (one per collection and index, up to ~1,300 connections). Each open handle costs
memory outside the cache and pins pages in the cache: about 2 GB of cache is in use although the suite's data is small.
Two consequences (AB#8511, reopened 2026-11-16):

- **Cache 3 GB, not 1 GB.** With 1 GB the cache was full during the startup burst, when the parallel fixtures import
  their CK models and create their system tenants, and WiredTiger rolled transactions back under cache pressure
  (`transaction rolled back because of cache overflow`; CI builds 70416 and 70685, the system tenant was then missing
  and up to 36 tests failed). At 3 GB the measured cache use stays below 2 GB, under WiredTiger's 80 % eviction target.
- **Idle data handles are closed after 30 s** (`--setParameter wiredTigerFileHandleCloseIdleTime=30`, scan interval
  10 s, minimum 250; mongod's default is 600 s). The databases of a fixture whose tests are done stay idle for the rest
  of the run. Without it the 3 GB cache raised the mongod peak to 9.1 GiB; with it the peak is 6.7 GiB and at most
  ~91,000 handles are open at once.

Measured 2026-11-16 on a full run (1,454 tests). A 3 GiB limit was OOM-killed mid-run (tests then fail with
`Connection refused`). When that happens, measure the peak again (`docker exec <container> cat /sys/fs/cgroup/memory.peak`
during a full run; cache use: `serverStatus().wiredTiger.cache["bytes currently in the cache"]`) and raise the limit,
or let the fixtures drop their databases.

Locally the Docker VM must hold the mongod peak, the CrateDB containers (up to 3 GiB) and whatever else runs in it. On a
14 GiB Docker Desktop VM with two kind clusters running, the run without the idle-handle closing swapped and failed
with `MongoConnectionException` after two-minute stalls.

## Performance budget tests (`Category=Performance`, AB#8004)

Tests that assert a **wall-clock budget** (a p95 / median / elapsed ceiling that a slower machine can miss without any
code change) carry the xUnit trait `[Trait("Category", "Performance")]`. Today these are two classes:

| Test | Budget | Decision |
|---|---|---|
| `InterfaceQuery/GetRtEntitiesByInterfacePerformanceTests` (`IndexedSort_FirstPage_StaysWithinTheDecidedBudget`) | indexed first page over 30 collections p95 <= 100 ms; one collection median <= 1.25x the type query (+5 ms timer slack) | AB#6930 |
| `StreamDataDownsampling/DownsamplingManyIdsPerformanceTests` (`SevenDaysOverManyIds_StaysWithinFiveSecondsPerCall`) | downsampling of a time-range archive (3500 entities x 8 days of 15-minute windows, CrateDB testcontainer) over 400 ids and 7 days: every call < 5 s (limit 800 and 200, cold and warm, and 10 parallel calls of 350 ids), every bin complete. Seed ~2-4 min; `OCTO_TEST_DOWNSAMPLING_ENTITIES` raises the entity count (at least 3500, smaller values are refused) | AB#8993 |

### Why they are not in the PR / main CI

On a loaded CI agent the p95 budget failed main CI runs 62273, 62285 and 62304 (2026-10-30) without a regression - the same
commit was green on a re-run. A red main CI in this repository blocks the engine cascade and the release train. Decision
(Gerald, 2026-11-01): the normal CI (`azure-pipelines.yml`, Test step) runs with `--filter "Category!=Performance"`; the
category runs in its own pipeline, nightly and on demand. A breach is a signal, never a gate.

### Where the results are

- Pipeline **`octo-construction-kit-engine-mongodb-Performance`** (Azure DevOps, project OctoMesh, folder `\CI`), YAML
  `azure-pipelines-performance.yml` in this repository. Scheduled daily at 03:30 UTC on `main` (runs even without new commits),
  and *Run pipeline* for a manual run on any branch (parameter `entityCount`: 100 000 by default, 1 000 000 for the opt-in 1M run).
- Pool `meshmakers-ci-agents` (the tests need Docker). There is no dedicated perf agent; the quiet part is the time slot, between
  the auto-train start (Mon/Wed/Fri 01:00 UTC) and the morning jobs (docs collect 04:00, CI guard health 05:30 UTC).
- The run uses `pipelines/performance.runsettings` (`TreatNoTestsAsError`): if the trait or the test disappears, the filter
  selects nothing and the run goes red instead of reporting a silent green.
- The run's **Tests** tab shows pass/fail; the log of the step *Performance tests* shows every measured median / p95
  (`octo-progress` lines), also on a green run.
- **On a breach** (the *Performance tests* step failed; a restore/build failure files nothing) the run is red and its last step files a Bug under the Core Platform Sustain Feature (tags
  `perf-budget-breach`, `engine-mongodb`), or comments on the one that is still open (`pipelines/report-perf-breach.ps1`). The
  Bug step needs the project Build Service to be allowed to create and edit work items in the Core Platform area; without it,
  the step warns and the red run remains the signal.
- It is **not a gate**: no CI/PR trigger, not a branch policy, no pipeline resource of any other repo, publishes nothing, not
  part of the release train or the Orchestrator CI. Keep it that way.

Triage a red nightly run: was the agent busy (train, many builds at the same time)? Re-run it once by hand on a quiet agent.
Red again on a quiet agent = a real regression of the query path, a Bug for the Core team. The budget constants are part of the
AB#6930 decision; do not raise them to make a run green.

### Run them locally

Prerequisites: Docker running (Testcontainers starts MongoDB `8.0.x`; or `USE_LOCAL_MONGODB=true` against a local replica set),
the solution built (DebugL with the local package chain, or Release against the private feed).

```bash
# only the performance budget tests (VSTest filter; the test projects use xunit.v3.mtp-off)
dotnet test tests/Runtime.Engine.MongoDb.IntegrationTests -c DebugL --filter "Category=Performance"

# opt-in 1M run
OCTO_TEST_INTERFACE_QUERY_ENTITIES=1000000 dotnet test tests/Runtime.Engine.MongoDb.IntegrationTests -c DebugL --filter "Category=Performance"

# everything except them (what the CI runs; also the local full suite, see "Load-sensitive tests" below)
dotnet test Octo.ConstructionKit.Engine.MongoDb.sln -c DebugL --filter "Category!=LoadSensitive"

# the xUnit v3 executable directly
dotnet tests/Runtime.Engine.MongoDb.IntegrationTests/bin/DebugL/net10.0/Meshmakers.Octo.Runtime.Engine.MongoDb.IntegrationTests.dll -trait "Category=Performance"
```

Numbers measured on a developer machine: repository `CLAUDE.md`, section *Interface query*.

## Stress tests (`Category=Stress`, AB#8679 / AB#8680)

Tests that put a component under real load for minutes carry `[Trait("Category", "Stress")]`. Today that is the
`StreamDataWriteLoss` memory-pressure pair with its own CrateDB (512 MB heap, query breaker set per test,
`CrateMemoryPressureFixture`):

| Test | Proves | Fast coverage in the CI |
|---|---|---|
| `ArchiveWriteUnderMemoryPressureIntegrationTests` | guarded and unguarded archive writes store what they report or throw `StreamDataWriteIncompleteException` (AB#8679) | `MissingRowRetryTests`, `ConflictOutcomeTests` (unit), `StreamDataWriteLoss/ArchiveWriteShortfallIntegrationTests` (a `CHECK` constraint makes CrateDB drop rows without an error, < 1 s) |
| `RollupUnderMemoryPressureIntegrationTests` | forward aggregation and recompute write every row or fail loudly; a failed recompute keeps the previous generation (AB#8680) | `RollupSourceGenerationReadTests`, `CrateDbArchiveRecomputeExecutorTests` (unit), `StreamDataWriteLoss/RollupShortfallIntegrationTests` (a wrapped client deletes rows after the statement, < 1 s each) |

Decision (Reimar, 2026-11-09): stress tests do not belong in the integration suite. They took a local full run from about
13 to more than 22 minutes. The CI Test step (`azure-pipelines.yml`) runs with `--filter "Category!=Performance&Category!=Stress"`;
the stress tests run in the pipeline `octo-construction-kit-engine-mongodb-Performance` together with the budget tests
(`--filter "Category=Performance|Category=Stress"`, nightly 03:30 UTC and on demand). Unlike a budget breach, a red stress
test is a correctness finding: the write lost rows silently or the recompute flipped to an incomplete generation. A
stress test that saw no breaker trip is skipped with that reason, it never passes without proving anything.

Run them locally (Docker running, solution built in DebugL):

```bash
# only the stress tests
dotnet tests/Runtime.Engine.MongoDb.IntegrationTests/bin/DebugL/net10.0/Meshmakers.Octo.Runtime.Engine.MongoDb.IntegrationTests.dll -trait "Category=Stress"

# what the CI runs (without performance and stress tests)
dotnet tests/Runtime.Engine.MongoDb.IntegrationTests/bin/DebugL/net10.0/Meshmakers.Octo.Runtime.Engine.MongoDb.IntegrationTests.dll -trait- "Category=Performance" -trait- "Category=Stress"
dotnet test Octo.ConstructionKit.Engine.MongoDb.sln -c DebugL --filter "Category!=Performance&Category!=Stress"
```

A new test belongs in `Stress` when it needs sustained load (parallel writers, a tightened breaker, large data) to show its
effect. Keep a fast test of the same rule in the CI that produces the condition on purpose (an injected client, a SQL
constraint), so a regression is caught by every PR and not only overnight.

## Load-sensitive tests (`Category=LoadSensitive`)

Every `Performance` and `Stress` test also carries `[Trait("Category", "LoadSensitive")]`. The same trait and filter are
introduced in `octo-asset-repo-services` for its wall-clock tests. The local full suite then does not fail when other builds or test suites
run on the same machine (several agent lanes, a second checkout):

```bash
# local full suite: pre-commit check and recorded verification
dotnet test Octo.ConstructionKit.Engine.MongoDb.sln -c DebugL --filter "Category!=LoadSensitive"
# the load-sensitive tests on their own, on a quiet machine, one suite at a time
dotnet test tests/Runtime.Engine.MongoDb.IntegrationTests -c DebugL --filter "Category=LoadSensitive"
```

The CI filter (`Category!=Performance&Category!=Stress`) and the performance pipeline (`Category=Performance|Category=Stress`)
are unchanged; in this repository `Category!=LoadSensitive` selects the same tests as the CI filter.

### Adding a test that asserts a timing budget

- Put `[Trait("Category", "Performance")]` on the class (or the test) when a slower machine can fail it without a code change:
  p95 / median / throughput ceilings, ratios of two measured timings. Add `[Trait("Category", "LoadSensitive")]` next to it
  (and next to every `Stress` trait); `LoadSensitiveTraitTests` fails without it for a test of this assembly, and
  also when `LoadSensitive` appears on a test that is neither `Performance` nor `Stress` (the local filter and the CI filter
  would then select different tests).
- Do **not** use the category for timeouts that guard correctness and have a wide margin (a stale lock reclaimed "near-instantly"
  under 5 s, a cancellation that must interrupt a delay under 3 s, a statement timeout that must end a call under 30 s, a
  hosted-service tick awaited for up to 5 s). They stay in the CI; a miss there is a bug, not noise.
- Measurement tests that only run with an `OCTO_*` variable set (`DeleteCapMeasurementTests`, `RollupGraceDiskMeasurementTests`,
  `RollupLocalMeasurementTests`, `StagingCopyLocalS3Tests` measure mode, `RollupManualColumnsCostMeasurement`) assert no budget and
  skip in the CI; they need no category.
