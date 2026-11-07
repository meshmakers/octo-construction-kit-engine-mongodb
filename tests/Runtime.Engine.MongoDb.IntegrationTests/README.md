# Runtime.Engine.MongoDb.IntegrationTests

Integration tests of the MongoDB runtime engine against a Testcontainers MongoDB replica set (and, for the stream data
tests, a Testcontainers CrateDB). Layout, parallelism rules and the CI time budget: repository `CLAUDE.md`, section
*Test layout, parallelism rules and the CI time budget*.

## Container memory caps (AB#8511)

Every Testcontainer of this suite has a fixed memory footprint (`Fixtures/TestContainerLimits.cs`):

| Container | Setting | Container memory limit |
|---|---|---|
| shared mongod (`SharedMongoDbContainer`) | `--wiredTigerCacheSizeGB 1` | 12 GiB, no swap (measured peaks 6.4-8.4 GiB) |
| CrateDB (`BlueprintArchiveActivationFixture`, `CrateStagingCopyFixture`) | `CRATE_HEAP_SIZE=1g` | 3 GiB, no swap (measured peak 1.9 GiB) |

Without them mongod sized its WiredTiger cache from the Docker host (125 GiB on the CI node). With several suites on
one node, the agents' shared Docker cgroup was throttled as a whole: tests stalled for minutes, MongoDB aborted
transactions after its 60 s `transactionLifetimeLimitSeconds` and CrateDB DDL ran past its 30 s budget. Do not raise
the transaction lifetime or a test budget to get around a stall; a suite that needs more memory than the caps is a
finding. `TestContainerLimitsTests` asks the running mongod for its cache size and its memory limit.

The mongod memory is not the cache: every fixture keeps its databases in the shared server (~70,000-106,000 open
WiredTiger data handles at the end of a run, up to ~800 connections), so the footprint grows with the number of
fixtures. A 3 GiB limit was OOM-killed mid-run (tests then fail with `Connection refused`). When that happens, measure
the peak again (`docker exec <container> cat /sys/fs/cgroup/memory.peak` during a full run) and raise the limit, or let
the fixtures drop their databases.

## Performance budget tests (`Category=Performance`, AB#8004)

Tests that assert a **wall-clock budget** (a p95 / median / elapsed ceiling that a slower machine can miss without any
code change) carry the xUnit trait `[Trait("Category", "Performance")]`. Today that is one class:

| Test | Budget | Decision |
|---|---|---|
| `InterfaceQuery/GetRtEntitiesByInterfacePerformanceTests` (`IndexedSort_FirstPage_StaysWithinTheDecidedBudget`) | indexed first page over 30 collections p95 <= 100 ms; one collection median <= 1.25x the type query (+5 ms timer slack) | AB#6930 |

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

# everything except them (what the CI runs)
dotnet test Octo.ConstructionKit.Engine.MongoDb.sln -c DebugL --filter "Category!=Performance"

# the xUnit v3 executable directly
dotnet tests/Runtime.Engine.MongoDb.IntegrationTests/bin/DebugL/net10.0/Meshmakers.Octo.Runtime.Engine.MongoDb.IntegrationTests.dll -trait "Category=Performance"
```

Numbers measured on a developer machine: repository `CLAUDE.md`, section *Interface query*.

### Adding a test that asserts a timing budget

- Put `[Trait("Category", "Performance")]` on the class (or the test) when a slower machine can fail it without a code change:
  p95 / median / throughput ceilings, ratios of two measured timings.
- Do **not** use the category for timeouts that guard correctness and have a wide margin (a stale lock reclaimed "near-instantly"
  under 5 s, a cancellation that must interrupt a delay under 3 s, a statement timeout that must end a call under 30 s, a
  hosted-service tick awaited for up to 5 s). They stay in the CI; a miss there is a bug, not noise.
- Measurement tests that only run with an `OCTO_*` variable set (`DeleteCapMeasurementTests`, `RollupGraceDiskMeasurementTests`,
  `RollupLocalMeasurementTests`, `StagingCopyLocalS3Tests` measure mode, `RollupManualColumnsCostMeasurement`) assert no budget and
  skip in the CI; they need no category.
