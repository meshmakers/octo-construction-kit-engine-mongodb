---
description: "Gap report of a number range per fiscal year: missing and duplicate numbers, the index it needs, the read filter, the format reader and the tests"
applies_to: src/Runtime.Engine.MongoDb/Repositories/PreDocumentModifications/NumberRangeGapReport*.cs, src/Runtime.Engine.MongoDb/Repositories/PreDocumentModifications/AutoIncrementNumberReader.cs, src/Runtime.Contracts.MongoDb/Repositories/NumberRangeGapReport.cs, tests/Runtime.Engine.MongoDb.IntegrationTests/AutoIncrement/NumberRangeGapReportTests.cs, tests/StreamData.UnitTests/AutoIncrement/AutoIncrementNumberReaderTests.cs
---

# Gap report of a number range

`ITenantRepository.GetNumberRangeGapReportAsync(session, numberRange, fiscalYear)` proves that a number range
(`System/AutoIncrement`, by its well-known name) handed out its numbers without gaps and without duplicates. The
schema it reads (`Format`, `StartValue`, `Periods`, `Release`) is described in the design note
`docs/concept-number-ranges-and-released-documents.md` of `octo-construction-kit-engine`.

## What it computes

- **Interval**: `StartValue` (or 1) to the current value - of the fiscal year's period for a range with
  `PeriodKind = FiscalYear`, of the range (`CurrentValue`) without periods. A fiscal year without a period entry has
  handed out nothing: the interval is empty.
- **Documents**: every concrete CK type with an attribute whose `autoIncrementReference` names the range, one
  aggregation per type and attribute. Only released documents count: for a range with `Release`, the documents whose
  `StateAttribute` holds one of the `ReleasedValues` (an enum value by its key or name, a string as it is). Drafts carry
  no number and are not read. A range without `Release` counts every numbered document.
- **Reading a number back**: `AutoIncrementNumberFormat.CreateReader(fiscalYear)` gives an `AutoIncrementNumberReader`.
  It reads a text only when formatting the value it carries gives the same text again (`RE-2027-0007` is not a number
  of `RE-{yyyy}-{counter:D5}`, `RE-2026-00007` not one of 2027). A legacy `string.Format` format is read the same way;
  a specifier that writes more than digits (e.g. a group separator) is not read. An Int number attribute is taken as
  it is.
- **Result** (`NumberRangeGapReport`): `Missing` as ascending intervals (`From`..`To`, with the formatted numbers),
  `Duplicates` (counter value, number, every document id), `Irregular` (a number outside the interval or without the
  format; at most 1000 listed, `IrregularCount` counts all) and `DocumentCount`. The values seen are a bit set over the
  interval (at most 100 million numbers); the documents of a duplicate value are collected by a second indexed read
  (`$in` on the duplicate numbers).
- **Arguments**: a range with fiscal-year periods needs `fiscalYear`, one without refuses it (`ArgumentException`); a
  range that is not valid is refused with `AutoIncrementFailedException.InvalidNumberRange`. An unknown range answers
  null.

## Index and query plan

The report reads through an index on the number attribute, which the **document model** declares on its document type
(design note D12: never on a shared base type such as Basic `Document`; the engine creates no index at run time):

```yaml
indexes:
  - indexType: "Ascending"
    fields:
      - attributePaths:
          - Number
```

The engine builds it as `(ckTypeId, attributes.number, _id, rtState)`. The `$match` of the report is
`ckTypeId = <type>`, the number - for a String number the anchored prefix of the fiscal year
(`^\QRE-2027-\E`, the literal text before the counter; MongoDB turns it into the bounds
`["RE-2027-", "RE-2027.")`), for an Int number any number - the released states and the caller's read filter; the
`$project` keeps `_id` and the number. The plan is an `IXSCAN` of that index with a `FETCH` that filters the state.
Without the index MongoDB falls back to the type's other indexes or a collection scan of the type's collection: the
report still answers, but reads every document of the type.

## Who sees what

The report runs in the caller's session, never as the system: the range and the documents are read with the caller's
data-permission read filter (`IDataSecurityFilterFactory`, rendered by `DataSecurityFilterRenderer`, the filter of the
generic queries). A caller who may not read the range gets null, as for a range that does not exist. A document the
caller may not read is not counted, so its number shows as missing; the report never names a hidden document.

## Tests

- Unit: `AutoIncrementNumberReaderTests` (prefix, what is read, what is refused).
- Integration against a real MongoDB, `AutoIncrement/NumberRangeGapReportTests`, model `GapReportTest` (Invoice with
  the index on `Number`): a clean range 1..10 of fiscal year 2027; a seeded gap (4) and duplicate (7 twice, inserted
  bypassing the allocator) with both document ids; two fiscal years reported separately; drafts not counted; irregular
  numbers; the explain plan (`IXSCAN` of the number index with the prefix bounds, no `COLLSCAN`, written to the test
  output); a clean report from numbers the allocator handed out; a range without periods.
