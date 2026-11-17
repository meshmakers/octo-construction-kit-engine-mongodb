# Review focus: octo-construction-kit-engine-mongodb

Check these points in addition to the general review. Each point names its source: a section of `CLAUDE.md` or the pull
request whose review found the problem.

## Public repository

- Changed docs, code comments and test names carry no internal hostnames, customer or tenant names or secrets (the
  repository is public; same rule as the service repositories that consume this engine).

## Sensitive data

- Hidden, `securitySensitive` and `Secret` attribute values never reach a log, a profiler entry, an error message or a
  CrateDB statement text, and hidden archive columns stay redacted on read and import (`CLAUDE.md`, "Observability -
  MongoDB Command Profiling"; PR #252, PR #257, PR #258).

## Queries and indexes

- A page of a type query sorts totally: the caller's keys followed by the `_id` tiebreak; a shortcut through a unique
  index applies only to a single-key index inside the index's type set (`CLAUDE.md`, "Page order of runtime queries: the
  _id tiebreak"; PR #253).
- An index hint names only an index that provides the sort and is complete (never partial, sparse, hidden or collated),
  and the index cache cannot hand out a stale entry after invalidation (`CLAUDE.md`, "Deep sorted pages: the index
  hint"; PR #256).
- An index upgrade compares the full definition (fields, partial filter, type filter, text index found by content, not
  name) and never drops the old index before the replacement build succeeded (PR #253).

## Writes and consistency

- Repository clients keep `w: majority` and are released, not only dropped; a write-concern-only failure keeps its cause
  (`CLAUDE.md`, "Repository clients are released", "Write and read concern", "Write-concern-only failures keep their
  cause").
- Updates with an expected `rtVersion` match the version in the filter and increment it in the same operation
  (`CLAUDE.md`, "Expected rtVersion").
- Tenant and backup operations are bound to the original tenant registration, and pending notifications are dropped when
  the transaction removes the tenant (PR #259).

## Concurrency and CK import

- Caches shared across callers (CK import lock, index cache) coalesce concurrent misses, bound their reads with a
  timeout, and cannot be bypassed by an importer of another version (PR #248, PR #256).

## Tests

- Integration tests assert actual values (not only counts), clean up tables and locks when setup or the test fails, and
  avoid timing-dependent assertions (PR #249, PR #250, PR #253, PR #258).
