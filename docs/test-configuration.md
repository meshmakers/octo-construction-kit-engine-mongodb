---
description: "Integration test setup: Testcontainers or local MongoDB, CrateDB images, suite layout, parallelism, CI time budget, test data"
applies_to: tests/**, azure-pipelines.yml, azure-pipelines-performance.yml
---

## Test Configuration

### Switching between Testcontainers and Local MongoDB

Tests can run against either Testcontainers (default) or a local MongoDB instance.

**Option 1: Environment Variable**
```bash
USE_LOCAL_MONGODB=true dotnet test -c DebugL --filter "Category!=LoadSensitive"
```

**Option 2: appsettings.test.json**
```json
{
  "systemTest": {
    "useLocalDatabase": true,
    "localDatabaseHost": "localhost:27017"
  }
}
```

### CrateDB image and the 5.x / 6.x compatibility tests

The CrateDB tests (`StagingCopy/`, `CrateCompat/`) run on `crate:6.4.6`. `OCTO_TEST_CRATEDB_IMAGE` replaces the image, so the same tests
run on every supported CrateDB line (for example `crate:5.10.10`):

```bash
OCTO_TEST_CRATEDB_IMAGE=crate:5.10.10 dotnet tests/Runtime.Engine.MongoDb.IntegrationTests/bin/DebugL/net10.0/Meshmakers.Octo.Runtime.Engine.MongoDb.IntegrationTests.dll \
  -namespace Meshmakers.Octo.Runtime.Engine.MongoDb.IntegrationTests.CrateCompat
```

The `CrateCompat` tests pin what a CrateDB version answers and accepts, and must pass on every supported line: the bind parameter types of the
archive INSERTs per column type (`CrateInsertTypeCompatibilityTests`; 6.4 checks the VALUES types more strictly than 5.x), the error
texts the classifiers match on (`CrateErrorContractTests`; `IsMissingTableError`, `IsColumnAlreadyExists`, the reset `ROLLBACK`) and
the format-1 import of array and record columns (`CrateFormatOneImportArrayTests`). A fix for one line must keep the other working.
CrateDB 6.2 renumbered its HTTP error codes (4100 to 40910, 5030 to 5005); the platform talks to CrateDB over the PostgreSQL wire
protocol only and does not read them.

### The integration suite is silent for most of its run — that is not a hang

At the default `dotnet test` console verbosity **a passing test prints nothing**: only `[FAIL]` and
`[SKIP]` lines reach the log. This suite reports its last failing test very early — the stream-data
collections abort on their first assertion and are done inside the first two minutes — and then keeps
working for the rest of its wall clock, so the log falls silent for the remainder. Measured on the
`ae3336e` commit, all six runs, same shape:

| Build | Result | Silence before `Results File:` | Integration wall clock | Share |
|---|---|---|---|---|
| 48277 | red | **1177 s** | 1319 s | 89 % |
| 48271 attempt 1 / 2 / 3 | red | 341 s / 440 s / 649 s | 406 / 512 / 779 s | 84-86 % |
| 48116 | **green** | 520 s | 604 s | 86 % |
| 48223 | **green** | 409 s | 461 s | 89 % |

**The silence is the tests running.** The green builds are silent at exactly the same place, so it
cannot be a failed cleanup stalling. ADO's own per-test figures for the integration run confirm where
the wall clock goes: 1042-1927 s of aggregate test time across the 482 results, **slowest single test
69-99 s** (not 7 s — that figure comes from reading only the failed results). The sum exceeds the wall
clock because `parallelizeTestCollections: true` runs the collections concurrently. Reproduced locally:
a fully green DebugL run printed four lines in its first 11 s, then nothing for 166 s of its 176 s,
and the .trx `<Times finish>` equals the last test's `endTime` to the second — zero post-run gap.

Two consequences worth keeping:

- **Never conclude "the build hangs" from a quiet log here.** Read `[xUnit.net ...] octo-progress`
  lines (below) or the .trx times first. Misreading this once cost real time — the run was reported as
  stuck for twenty minutes while the actual cause, the `System.StreamData` resolve failure, had been in
  the log within seconds.
- **The cost driver is fixture setup, and it is invisible in every per-test duration.** Each of the
  ~17 collection fixtures creates its own system tenant and imports the CK models; xUnit attributes
  that to no test at all. If this suite needs to get faster, that is the place, not the tests.

### Progress and bounded teardown

`Fixtures/RunProgress.cs` is the suite's progress channel. It writes **xUnit diagnostic messages**
(`diagnosticMessages: true` in `xunit.runner.json`), which the runner prints as
`[xUnit.net HH:MM:SS.ff] ... octo-progress <elapsed> <what>` — the same channel the `[FAIL]` lines use.
`Console.WriteLine` from a fixture does **not** work for this: it is captured as test output and only
ever reaches the .trx, which is why "Using shared Testcontainer MongoDB at ..." was never visible in a
CI log. Every fixture reports `initialising` / `ready in Xs`, and `RunHeartbeatFixture` (an assembly
fixture) adds a line every 30 s, so the log can no longer go quiet for longer than that — a heartbeat
that keeps ticking means "working", one that stops means "hung", and the last `octo-progress` line says
where. The diagnostic sink hangs off the `AsyncLocal` test context, so the heartbeat thread would see
only xUnit's idle context: `RunProgress.CaptureDiagnosticSink()` captures it once from the assembly
fixture.

`RunProgress.RunBoundedAsync` is the only way teardown runs: each step gets a time budget (60 s for
fixture teardown and provider dispose, 30 s for a throwaway tenant), is reported before and after, and
**never throws**. A cleanup that overruns is abandoned with a `STILL RUNNING ... abandoned` line and the
run continues — cleaning up after a failure, against a tenant in an unknown state, is exactly the place
a finished run could otherwise be held open. Tests that create a throwaway tenant drop it through
`Fixtures/ThrowawayTenant.cs` in a `finally`, so a failing test no longer leaves `streamdrop*` tenants
and their databases behind in the shared container.

Swallowing a teardown failure loses it as a test result, deliberately: the reported line is the only
trace, and the run's red/green verdict is already decided by the tests themselves.

### Test layout, parallelism rules and the CI time budget

**Layout.** `tests/StreamData.UnitTests` (assembly `Runtime.Engine.UnitTests`, ~940 tests, seconds) and
`tests/Runtime.Engine.MongoDb.IntegrationTests` (~590 tests against ONE shared Testcontainers MongoDB
replica set, `Fixtures/SharedMongoDbContainer.cs`). Integration tests are grouped into xUnit collections
(`Collections/*.cs`), each with a fixture that creates its own system tenant/database; the CK v2
round-trip gate lives in `RoundTrip/` (`CkMetaModelRoundTripTests`, `CkMetaModelReflectionGateTests`,
`CkMetaModelCacheRoundTripTests` in `CkModelImportMigrationCollection`; `CkV2CompatibilityTests` in
`CkModelImportGuardCollection`; `ArchiveHiddenAccessTests` with its own fixture).

**Parallelism rules for new integration tests** (`xunit.runner.json`: `parallelizeTestCollections: true`,
`parallelizeAssembly: false`):

- Tests inside one collection run serially; collections run concurrently. The wall clock is the
  longest collection chain plus fixture setup, not the sum of all tests. Today the critical path is
  `BlueprintServiceIntegrationTests` (~31 tests, one collection) and `DirectedRoleDeepGraphTests`.
- `SeedValueGuardBlueprintIntegrationTests` (`TestBlueprints/.../SeedGuardBp`, CK type
  `Test/SeedGuardEntity`) proves the engine's seed value guard against a real tenant: a blueprint update
  with empty/omitted seed values keeps non-empty tenant values (Merge and Full), reports them, applies them
  only with `AllowBlanking` (or a `ConfirmedBlankings` pair), and an omitted attribute at its CK default is not
  reported. A non-empty JSON seed that empties a field is reported as `SeedEmptiesJsonField` with the field's
  path, never as `SeedEmpty`. It has its own
  collection. Adding CK elements to `TestCkModel` also requires the golden
  `testData/ckv2-golden-main-v1-ck-documents.txt` to be extended (`CkV2CompatibilityTests`).
- Put a new long-running class into its own collection (own fixture or a fixture shared only where
  the state really is shared) instead of appending to the longest one; a new fixture costs one tenant
  setup (~2-5 s locally, 15-110 s on a loaded agent).
- Never mutate process-wide state from a parallel collection (see the schema-prefix note below).

**Measured (2026-10-09).** Locally (Mac mini, 14 cores, DebugL, `dotnet test <sln>`):
integration suite 590 passed / 2 skipped in **4 m 07 s**; with the 45 new CK v2 / RoundTrip tests
filtered out it is **also 4 m 07 s** - they add ~50 s of summed test time (7.5 %) but none to the
critical path. On CI the same suite took 9 m 46 s (r3.5.0, 50936, 545 tests) and 21-31 min after
the CK v2 work (51028, 51030, 51015), but every pre-existing class slowed down by the same factor
(x2.8), the bare MongoDB container start went from 22 s (burst agent) / 43 s to 65 s, and the very
same commit ran in 5 min on a burst agent (50769) and 38 min on a loaded one (50755). The CI growth is
agent load (CPU steal on the CI host), not the new tests.

**CI time budget (`azure-pipelines.yml`).** Target: Test step < 15 min on an uncontended agent (today
~5-10 min). The Test step has **no** `retryCountOnTaskFailure` (a whole-step retry reran a deterministic
red suite three times into the job timeout, 51009/51014, and hid a mass failure, 50546), its own
`timeoutInMinutes: 50` below the explicit job `timeoutInMinutes: 70`, and `--blame-hang-timeout 15m`
(no dump) so a single hung test aborts the run and is named in the log. Flaky tests are fixed or
handled per test, never by retrying the step.

**Container memory caps.** The shared mongod runs with `--wiredTigerCacheSizeGB 3`, closes idle WiredTiger
data handles after 30 s (`wiredTigerFileHandleCloseIdleTime`) and has a 12 GiB container limit (measured peak 6.7 GiB,
mostly the data handles of the fixture databases, not data). A 1 GB cache was full during the startup burst and
WiredTiger rolled transactions back (`transaction rolled back because of cache overflow`, CI 70416/70685); every CrateDB test
container with a 1 GB heap and a 3 GiB limit (`Fixtures/TestContainerLimits.cs`, pinned by
`TestContainerLimitsTests`). Without them one suite took memory from the whole CI node and several suites at once
stalled until MongoDB aborted transactions after 60 s. Never raise `transactionLifetimeLimitSeconds` or a test budget
to hide a stall. Details: `tests/Runtime.Engine.MongoDb.IntegrationTests/README.md`.

**Container CPU limits and bounded parallelism.** Every Testcontainer also has a CPU limit (Docker `NanoCpus`:
mongod 4 CPUs, every CrateDB container 2 CPUs; measured peaks 1.9 and 2.4 CPUs without a limit) and the CrateDB heap is set
explicitly for all three CrateDB fixtures (1 GB, the stress container 512 MB), all in `Fixtures/TestContainerLimits.cs`
(`Resources(memory, cpus)`, pinned by `TestContainerLimitsValueTests`, `TestContainerLimitsTests` and `CrateTestContainerLimitsTests`). `xunit.runner.json`
sets `maxParallelThreads: 8` (the xUnit default is the number of logical CPUs: 22 collections started at once on a developer
laptop; the CI agents already ran 8). Sum of the limits: 18 GiB / 8 CPUs for the CI suite, 21 GiB / 10 CPUs with the stress
container; a local Windows run needs a Docker Desktop VM of at least 20 GiB and 12 CPUs (`.wslconfig`). The two CrateDB tests
that open their own pools (`DownsamplingPhaseProbePoolIntegrationTests`, `CrateConnectionPoolReturnTests`) wait up to 30 s for
the container first and name it when it does not answer, then open the connections of their pool once, one after the other, and give them back (this removes the initial connects while the warmed connections stay healthy; a broken one is still replaced within 3 s; `ADiscardedConnection_FreesItsPoolSlot` has no warm-up, its own one-connection data source must connect twice within 3 s each)
before the measured part (`StagingCopy/CrateTestConnect.cs`, `WarmUpAsync`): on Windows a new connection through the Docker
Desktop port proxy sometimes takes seconds while CrateDB is idle. The pool-wait tests keep their 3 s connect timeout on purpose. Local run on Windows and the measurements: `tests/Runtime.Engine.MongoDb.IntegrationTests/README.md`,
*Container resources and parallelism*.

**Performance budget tests are not in the CI.** The Test step runs with
`--filter "Category!=Performance"`. Tests that assert a wall-clock budget carry
`[Trait("Category", "Performance")]` (today `GetRtEntitiesByInterfacePerformanceTests`, with the interface-query budget, and `DownsamplingManyIdsPerformanceTests`, budget < 5 s per downsampling call over 400 ids and 7 days) and run only in the
separate pipeline `octo-construction-kit-engine-mongodb-Performance` (`azure-pipelines-performance.yml`: daily 03:30 UTC on
`main` + manual, pool `meshmakers-ci-agents`, a breach turns the run red and files/updates a Bug tagged `perf-budget-breach`
under the Core Sustain Feature via `pipelines/report-perf-breach.ps1`). It is not a gate: no trigger, no branch policy, no
pipeline resource, publishes nothing. Main CI runs 62273/62285/62304 (2026-10-30) failed on agent load alone, which blocked the
cascade. Correctness timeouts with a wide margin (`RepositoryDistributedLockServiceTests` 5 s / 3 s,
`StagingCopyIntegrationTests` 30 s, the orchestrator tick waits, `TenantUpgradeGuardTests` 2 s for 100 in-memory models) stay in
the CI. Local run, triage and the classification rule: `tests/Runtime.Engine.MongoDb.IntegrationTests/README.md`.

**Stress tests are not in the CI either.** Trait `[Trait("Category", "Stress")]`: the
`StreamDataWriteLoss/*UnderMemoryPressureIntegrationTests` with their own small-heap CrateDB (`CrateMemoryPressureFixture`).
They took a local full run from ~13 to more than 22 minutes (decision Reimar 2026-11-09: stress tests do not belong in the
integration suite). The CI filter is `Category!=Performance&Category!=Stress`; the pipeline
`octo-construction-kit-engine-mongodb-Performance` runs `Category=Performance|Category=Stress`. Locally:
`dotnet tests/Runtime.Engine.MongoDb.IntegrationTests/bin/DebugL/net10.0/Meshmakers.Octo.Runtime.Engine.MongoDb.IntegrationTests.dll -trait "Category=Stress"`.
The rules they prove stay in the CI as fast tests that produce the shortfall on purpose: `MissingRowRetryTests`,
`StreamDataWriteLoss/ArchiveWriteShortfallIntegrationTests`, `StreamDataWriteLoss/RollupShortfallIntegrationTests`.

**Local full suite: leave out `Category=LoadSensitive`.** Every `Performance` and `Stress` test also carries
`[Trait("Category", "LoadSensitive")]`; `LoadSensitiveTraitTests` fails when a test of the integration test assembly
lacks it, and when a test there carries `LoadSensitive` without `Performance` or `Stress`. `StreamData.UnitTests` is not
scanned: it has no `Performance`, `Stress` or `LoadSensitive` test; keep it that way or extend the guard. The same trait
and filter are introduced in `octo-asset-repo-services` for its wall-clock tests. The local full suite then stays
deterministic while other builds or suites run on the machine:

```bash
# local full suite (pre-commit check, recorded verification) - same set as the CI Test step
dotnet test Octo.ConstructionKit.Engine.MongoDb.sln -c DebugL --filter "Category!=LoadSensitive"
# the load-sensitive tests on their own, one suite at a time on a quiet machine
dotnet test tests/Runtime.Engine.MongoDb.IntegrationTests -c DebugL --filter "Category=LoadSensitive"
```

A new test that asserts a wall-clock budget or needs sustained load gets `Performance` or `Stress` **and**
`LoadSensitive`. A correctness timeout with a wide margin (a bounded wait that ends instead of hanging) is not
load-sensitive and stays in the suite.


## Test Data Structure

The test CK model includes this hierarchy:
```
Europe (Continent)
└── Österreich (Country)
    ├── Salzburg (StateOrProvince)
    │   ├── Pinzgau / Zell am See (District) → Fusch (Municipality)
    │   ├── Tennengau, Pongau, Lungau, Flachgau (Districts)
    │   └── Salzburg Stadt (District) → Leopoldskron-Moos (Municipality)
    └── Tirol (StateOrProvince)
        ├── Lienz, Landeck (Districts - active)
        └── Imst, Kitzbühel (Districts - Archived)
```


### CK v2 Kitchen Sink

`TestCkModelKitchenSink` (`KitchenSink-1.0.0`, `ckLanguage: 2`) uses every CK v2 construct: interfaces
`Named-1` (required + optional member) and `Coded-1`; abstract `Thing` implements Named, `Gadget` derives from
Thing (inherits Named) and implements Coded, unrelated `Widget` implements Named; all four `access` values on type and
record (`Address`) assignments, and `ReadWrite` / `ReadOnly` / `MethodOnly` on the association role `Link` (Hidden is
not allowed on association roles, engine rule 108); methods `ChangePassword-2` (every field),
`Reindex-1` (static) and `Ping-1` (minimal) on Thing. Extend it whenever a new meta-model field is added.
Since that change (message 129) internal elements may only be referenced by internal ones: `Gadget`, `Widget`, `Coded-1`,
`Labeled-1` and `Legacy-1` are internal, and the internal attribute `Hint` (assigned by Widget) and the unreferenced
internal record `Memo` keep every visibility kind covered (C# twin `CkV2KitchenSinkModel` likewise).

### Migration Test Data

`TestCkModelV2` provides a v2.0.0 variant of the test CK model with:
- A migration script (`1.0.0-to-2.0.0.yaml`) that renames `Name` → `DisplayName`
- Migration metadata (`migration-meta.yaml`)

Used by `CkModelImportMigrationTests` to verify automatic migration on import.
