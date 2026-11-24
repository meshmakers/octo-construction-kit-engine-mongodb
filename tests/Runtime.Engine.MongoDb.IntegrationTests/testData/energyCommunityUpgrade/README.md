# Energy-community AutoIncrement upgrade fixture (AB#9756)

Read-only copies, used by `AutoIncrement/EnergyCommunityAutoIncrementUpgradeTests.cs` to prove that the
System 2.8.0 -> 2.9.0 upgrade keeps existing document and customer numbers.

| File | Source |
|------|--------|
| `ck-basic-2.yaml` | `octo-construction-kit` `Octo.Sdk.Packages.Basic`, compiled output `Basic-2.7.0` (main 55dcee4), compiled against `System-2.8.0` |
| `ck-basic.energy-2.yaml` | `Octo.Sdk.Packages.Basic.Energy`, `Basic.Energy-2.1.0`, same build |
| `ck-energycommunity-5.yaml` | `Octo.Sdk.Packages.EnergyCommunity`, `EnergyCommunity-5.1.0`, same build |
| `energy-community-counters.yaml` | `energy-community` `EnergyCommunity.Base` seed, the two counters (commit 662a954) |

The compiled models are the files the CK compiler wrote, unchanged. Compiled against System 2.9.0 they differ only
in the exact `System-2.9.0` entry of `dependencies` (checked 2026-11-24). Do not edit them by hand; to refresh, copy
the compiler output again and update this table.
