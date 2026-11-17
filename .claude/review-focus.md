# Review focus: octo-construction-kit-engine-mongodb

Check these points in addition to the general review. Each point names its source: a section of `CLAUDE.md` or the pull
request whose review found the problem.

## Sensitive data

- Hidden, `securitySensitive` and `Secret` attribute values are masked in every rendered command (profiler, logs), as
  far as the documented coverage goes (`CLAUDE.md`, "Credential redaction in every rendered command"; PR #252).
- Backups (native copy, row export) leave Hidden archive columns out (`CLAUDE.md`, "Backups leave Hidden columns out";
  PR #257).

## Queries and indexes

- A page of a type query sorts totally: the caller's keys followed by the `_id` tiebreak; the tiebreak is skipped only
  when the sort starts with all keys of a unique index (same directions or all reversed) that carries exactly the
  engine's type filter (`CLAUDE.md`, "Page order of runtime queries: the _id tiebreak"; `PagingTiebreakGate.cs`; PR #253).
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
- The tenant-created notification is published only after the commit, and not for a tenant the same transaction removed
  (`CLAUDE.md`, "Tenant-created notification after the commit"; PR #259).

## Concurrency and CK import

- The CK import lock cache coalesces concurrent misses, bounds its read with a timeout, and cannot be bypassed by an
  importer of another version (`CkModelImportLockCache.cs`; PR #248).

## Tests

- Integration tests assert actual values (not only counts), clean up tables and locks when setup or the test fails, and
  avoid timing-dependent assertions (PR #249, PR #250, PR #253, PR #258).
