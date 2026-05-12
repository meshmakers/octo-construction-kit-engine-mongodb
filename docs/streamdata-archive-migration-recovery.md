# StreamData Archive Migration Recovery (System.StreamData 1.1.0 → 1.2.0)

The Phase 2 type-rename migration that introduced the abstract `Archive` base
plus the concrete `RawArchive` / `RollupArchive` subtypes had a two-part bug
in the migration runtime, fixed in this repo at commit ${THIS_COMMIT}. Tenants
whose 1.1.0 → 1.2.0 migration ran before the fix may be in a partially-broken
state. This document is the manual recovery procedure.

## Symptoms

- The studio's archives list is empty for archives that were imported
  pre-Phase-2, even though `RtEntity_SystemStreamDataRawArchive` (or
  `…RollupArchive`, `…TimeRangeArchive`) holds documents.
- Some entities in `RtEntity_SystemStreamDataRawArchive` carry rollup-only
  attributes (`sourceArchiveRtId`, `bucketSizeMs`, `aggregations`, …) but their
  `ckTypeId` field reads `System.StreamData/RawArchive`.

## Root Cause

Two issues conspired:

1. **Mass-relabeling**: `GetRtEntitiesByTypeForMigrationAsync` loaded all
   entities from the source-type's physical collection without filtering on
   the `ckTypeId` field. Pre-Phase-2 the `CkArchive` collection held both
   `CkArchive` entities AND `CkRollupArchive` entities (because the latter
   derived from the former, with `CkArchive` as the only collection root). The
   `ChangeCkType CkArchive → RawArchive` step therefore caught the rollups
   too and silently re-tagged them all to `RawArchive`. The follow-up
   `CkRollupArchive → RollupArchive` step then found nothing left to migrate.

2. **Wrong physical collection**: `InsertOneRtEntityForMigrationAsync` wrote
   to `RtEntity_SystemStreamData<TargetType>` (the per-concrete-type
   collection), ignoring the new `DefiningCollectionRootCkTypeId`. After the
   Phase 2 schema bump the new collection root is the abstract `Archive` —
   the regular query path resolves through it, but the migrated entities
   ended up in the now-stale per-type collection and are invisible to the
   GraphQL / Studio data-source.

## Recovery Procedure

Run these MongoDB shell statements against each affected tenant DB. The
example below targets the `loxone` tenant.

### 1. Re-tag mis-classified rollups

Entities carrying rollup-specific attributes must be re-tagged.

```javascript
db.RtEntity_SystemStreamDataRawArchive.updateMany(
  {
    "attributes.bucketSizeMs": { $exists: true }
  },
  {
    $set: { "ckTypeId": "System.StreamData/RollupArchive" }
  }
);
```

(Use `attributes.period` instead of `bucketSizeMs` for time-range archives
that should be tagged `TimeRangeArchive`, if any landed in the wrong
collection.)

### 2. Move every archive entity to the abstract collection root

```javascript
const docs = db.RtEntity_SystemStreamDataRawArchive.find({}).toArray();
if (docs.length > 0) {
  db.RtEntity_SystemStreamDataArchive.insertMany(docs);
  db.RtEntity_SystemStreamDataRawArchive.deleteMany({
    _id: { $in: docs.map(d => d._id) }
  });
}
```

After step 2 the per-concrete-type collections are empty and can be dropped
(optional cleanup):

```javascript
db.RtEntity_SystemStreamDataRawArchive.drop();
db.RtEntity_SystemStreamDataRollupArchive.drop();      // if present
db.RtEntity_SystemStreamDataTimeRangeArchive.drop();   // if present
```

### 3. Verify

```javascript
db.RtEntity_SystemStreamDataArchive.find(
  {}, { _id: 1, ckTypeId: 1, rtWellKnownName: 1 }
).toArray();
```

Expected: every archive listed, each with the correct `ckTypeId`
(`RawArchive` / `RollupArchive` / `TimeRangeArchive`).

The studio's archives list should now display the entries on next refresh.

## Prevention

The engine fix in `TenantRepository.cs`:

- `GetRtEntitiesByTypeForMigrationAsync` now filters by exact-`ckTypeId`-field
  equality before returning. Derived types sharing the source-type's
  collection are no longer caught accidentally.
- `InsertOneRtEntityForMigrationAsync` now resolves
  `DefiningCollectionRootCkTypeId` from the CkCache for the target type and
  writes to that collection. Falls back to the per-type collection when the
  cache hasn't loaded the target yet (early-migration case where the new
  schema is still being installed).

New `ChangeCkType` migrations executed by post-fix engines do not exhibit
either issue.
