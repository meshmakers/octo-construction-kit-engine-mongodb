# Review focus: octo-construction-kit-engine-mongodb

Check these points in addition to the general review. Each point names its source: a section of `CLAUDE.md` or the pull
request whose review found the problem.

## Sensitive data

- Hidden, `securitySensitive` and `Secret` attribute values are masked in every rendered command (profiler, logs), as
  far as the documented coverage goes (`CLAUDE.md`, "Credential redaction in every rendered command"; PR #252).
- Backups (native copy, row export) leave Hidden archive columns out (`CLAUDE.md`, "Backups leave Hidden columns out";
  PR #257).

## Queries and indexes

- A page of a type query sorts totally: the caller's keys followed by the `_id` tiebreak. The tiebreak is left out only
  in the documented exceptions: a caller key already is `_id` (`IsSortUnique`); without a caller sort a `$geoNear` or a
  text search keeps its own order (`PreStagesDefineOrder`); `PagingTiebreakGate` finds a CK Ascending index in the legacy
  shape that provides the complete caller sort, or a CK Unique index with exactly the engine's type filter whose keys the
  sort starts with (same directions or all reversed); a `listIndexes` failure leaves sorted pages without it until the
  next check (`CLAUDE.md`, "Page order of runtime queries: the _id tiebreak"; `PagingTiebreakGate.cs`; PR #253).
- An index hint names only an index that provides the sort and is complete (never partial, sparse, hidden or collated),
  and the index cache cannot hand out a stale entry after invalidation (`CLAUDE.md`, "Deep sorted pages: the index
  hint"; PR #256).
- An index upgrade compares the full definition (fields, partial filter, type filter, text index found by content, not
  name) and never drops the old index before the replacement build succeeded, except the single text index of a
  collection (`CLAUDE.md`, "Page order of runtime queries: the _id tiebreak"; PR #253).

## Writes and consistency

- Repository clients keep `w: majority` and are released, not only dropped; a write-concern-only failure keeps its cause
  (`CLAUDE.md`, "Repository clients are released", "Write and read concern", "Write-concern-only failures keep their
  cause").
- Updates with an expected `rtVersion` match the version in the filter and increment it in the same operation
  (`CLAUDE.md`, "Expected rtVersion").
- A tenant created or attached inside the caller's transaction gets its tenant-created notification only after the
  commit (after-commit callback of the session), none on a rollback and none when the same transaction detached or
  dropped it afterwards; an engine operation that owns its transaction publishes through its `TenantNotificationScope`
  after its own commit; without a transaction the notification is published at once (`CLAUDE.md`, "Tenant-created
  notification after the commit"; PR #259).

## Concurrency and CK import

- The CK import lock cache coalesces concurrent misses, bounds its read with a timeout, and cannot be bypassed by an
  importer of another version (`CkModelImportLockCache.cs`; PR #248).

## Tests

- Integration tests assert actual values (not only counts), clean up tables and locks when setup or the test fails, and
  avoid timing-dependent assertions (PR #249, PR #250, PR #253, PR #258).
