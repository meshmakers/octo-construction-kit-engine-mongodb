---
description: "BSON conventions: TimeSpan ticks, camelCase, explicit field mappings, navigation property syntax"
applies_to: src/Runtime.Engine.MongoDb/Serialization/**
---

## BSON Serialization Conventions

### TimeSpan Attributes — Canonical Int64 Ticks (AB#4259)

`TimeSpan` attribute values are stored as BSON **Int64 ticks**. The dedicated
`TimeSpanSerializer` (`Serialization/`) writes `value.Ticks` and reads Int64/Int32/Double
ticks plus string shapes. Strings are tolerated because CK attribute values live in a
`Dictionary<string, object?>` that round-trips through `OctoObjectSerializer` (dispatches on
BSON type, not the consumer's CLR type), so the per-type serializer does not always apply.

The accepted string shapes are, in order:

1. **Bare-integer ticks string** (e.g. `"9000000000"`) — the shape the **ImportRt
   export/import JSON round-trip** produces for a `TimeSpan` attribute. It must be parsed as
   ticks, **not** handed to `TimeSpan.Parse`, which reads `"9000000000"` as 9-billion *days*
   and overflows to a parse failure.
2. `.NET` literal (`"00:15:00"`).
3. ISO-8601 duration (`"PT15M"`).

The same three-way coercion is mirrored in two engine-layer places that handle the dict's
`object?` values directly (the serializer is bypassed there):

- `AttributeValueConverter.ConvertAttributeValue` (`Runtime.Contracts`) — the **import
  normalization** point (`ImportRtModelCommand.AssignAttributes` →
  `RtTypeWithAttributes.SetAttributeValue`). Converting the ticks string to a real `TimeSpan`
  on import means the next Mongo write persists the canonical Int64.
- `RtTypeWithAttributes.TryCoerceTimeSpan` (`Runtime.Contracts`) — the **read** point behind
  the generated `GetAttributeValueOrDefault<TimeSpan>` accessor. Without it, a corrupted
  ticks-string value fell through to `Convert.ChangeType(string, TimeSpan)` and threw
  `InvalidCastException`, surfacing as the generic ASSET1002 "An error occurred" on
  `enableArchive` for an imported `TimeRangeArchive` (its `Period` is the only TimeSpan
  attribute in the StreamData model).

When adding a new place that coerces an attribute value to `TimeSpan`, accept the
bare-integer ticks string too, or it will reject already-imported data.

### CamelCase Convention

A global `CamelCaseElementNameConvention` is registered in `MongoRepositoryClient.cs`:

```csharp
ConventionRegistry.Register(OctoConventionCamelCase,
    new ConventionPack { new CamelCaseElementNameConvention() }, _ => true);
```

This means all C# properties are serialized to camelCase MongoDB field names:
- `RtAssociationRoleId` → `rtAssociationRoleId`
- `TargetRtCkTypeId` → `targetRtCkTypeId`
- `NavigationPropertyName` → `navigationPropertyName`
- `Attributes` → `attributes`

### Explicit Field Mappings

Some classes have explicit BSON mappings that override the convention:

**NavigationEnd** (`MongoRepositoryClient.cs`):
```csharp
BsonClassMap.RegisterClassMap<NavigationEnd>(cm =>
{
    cm.SetIgnoreExtraElements(true);
    cm.MapIdMember(c => c.AssociationId).SetElementName("_id");  // Explicit!
    cm.AutoMap();
});
```

**RtEntityGraphItem**:
```csharp
BsonClassMap.RegisterClassMap<RtEntityGraphItem>(cm =>
{
    cm.SetIgnoreExtraElements(true);
    cm.AutoMap();
    cm.MapMember(c => c.Associations).SetElementName(Constants.AssociationName);  // "_associations"
});
```

### Important for Aggregation Pipelines

When writing MongoDB aggregation pipelines with projections or AddFields:
1. Use camelCase for field names (matching the convention)
2. Check for explicit mappings that override the convention (e.g., `_id` for `AssociationId`)
3. Use `$fieldName` syntax when renaming fields in projections

## Navigation Property Syntax

Navigation properties use the following syntax:
```
navigationPropertyName.targetTypeName->attributeName
```

Example:
- `parent.testStateOrProvince->name` - Navigate via "Parent" association to StateOrProvince and get its Name attribute

### N:M Association Meta Properties

N:M associations use `::` separator for meta-properties (count/existence) to avoid collision with `->` attribute navigation:
```
navigationPropertyName.targetTypeName::totalCount    → count of associations
navigationPropertyName.targetTypeName::exists        → true if any association exists
```

Implementation in `SingleOriginRtQuery.CreateAssociationCountNavigation`:
- Uses `$lookup` + `$addFields($size)` + `$match` to count and filter by association count
- Triggered when `NavigationPair.AssociationCountFilter` is set
- Runs pre-pagination in `_associationStageDefinitions`, then enriches post-pagination via `CreateInnerNavigation`
