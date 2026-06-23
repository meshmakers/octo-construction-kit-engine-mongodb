using System.Security.Cryptography;
using System.Text;

using Meshmakers.Octo.ConstructionKit.Contracts;
using Meshmakers.Octo.ConstructionKit.Contracts.DataTransferObjects;
using Meshmakers.Octo.ConstructionKit.Contracts.DependencyGraph;
using Meshmakers.Octo.ConstructionKit.Contracts.Services;

using MongoDB.Bson;

namespace Meshmakers.Octo.Runtime.Engine.MongoDb.Repositories.MongoDb.Generic;

/// <summary>
/// Builds an actionable <see cref="SlowQueryIndexSuggestion"/> from a slow query's original
/// BSON command (Stage 2C / AB#4220). Called by <see cref="SlowQueryExplainParser.Parse"/>
/// whenever the explain plan reports <c>HasCollScan = true</c>; the resulting suggestion is
/// attached to the explain so a single fetch surfaces both the diagnosis and the proposed
/// fix.
/// </summary>
/// <remarks>
/// <para>
/// Filter-walking is pragmatic, not exhaustive. We enumerate the common operators and
/// classify each top-level filter key as Equality / Range / Sort. The compound index is
/// emitted per Mongo's ESR rule: equality keys first (most selective prefix), sort keys
/// second (cursor-friendly traversal), range keys last. This ordering is selective for every
/// prefix subset, which matters when a single suggestion may end up serving many filter
/// shapes that share a fingerprint.
/// </para>
/// <para>
/// <b>What we deliberately don't do:</b>
/// </para>
/// <list type="bullet">
///   <item>Call <c>getIndexes</c> to check for duplicates — adds per-fingerprint DB load and a
///         duplicate <c>createIndex</c> is a no-op anyway.</item>
///   <item>Auto-execute the suggestion — a one-click <c>createIndex</c> across N tenants is
///         footgun-shaped; manual copy-paste is the correct ergonomic for production data.</item>
///   <item>Split <c>$or</c> branches into per-branch indexes — we emit a single compound index
///         covering the union, with a <see cref="SlowQueryIndexSuggestion.Notes"/> entry that
///         the operator may want to split.</item>
/// </list>
/// </remarks>
public static class SlowQueryIndexSuggester
{
    /// <summary>Maximum field-walk recursion depth before we bail with a Notes caveat.</summary>
    private const int MaxWalkDepth = 8;

    /// <summary>Mongo's hard limit on index-name length, in bytes.</summary>
    private const int MaxIndexNameBytes = 127;

    /// <summary>
    /// Operator-prefixed keys that introduce a comparison rather than a field path. The values
    /// of these keys are operands, not nested documents to recurse into.
    /// </summary>
    private static readonly HashSet<string> EqualityOperators = new(StringComparer.Ordinal)
    {
        "$eq", "$in"
    };

    private static readonly HashSet<string> RangeOperators = new(StringComparer.Ordinal)
    {
        "$gt", "$gte", "$lt", "$lte", "$ne", "$nin", "$exists", "$type"
    };

    /// <summary>
    /// Operators that take their own indexing semantics and shouldn't be wedged into a regular
    /// compound index. We still record the field they appeared on, but tag a Notes caveat.
    /// </summary>
    private static readonly Dictionary<string, string> SpecialOperators = new(StringComparer.Ordinal)
    {
        ["$text"] = "$text operator detected — a text index is required, not a regular index.",
        ["$near"] = "$near operator detected — a geospatial (2dsphere) index is required.",
        ["$nearSphere"] = "$nearSphere operator detected — a geospatial (2dsphere) index is required.",
        ["$geoWithin"] = "$geoWithin operator detected — a geospatial index is recommended.",
        ["$geoIntersects"] = "$geoIntersects operator detected — a geospatial (2dsphere) index is required.",
        ["$elemMatch"] = "$elemMatch detected — a multikey index may be more appropriate than the suggested compound.",
        ["$regex"] = "$regex detected — index is only used for anchored patterns (^prefix); arbitrary regex still scans.",
        ["$all"] = "$all operator detected — a multikey index over the array field is required."
    };

    /// <summary>
    /// Boolean-combinator operators whose value is an array of sub-filter documents we recurse
    /// into. <c>$and</c> contributes fields as a normal union; <c>$or</c> / <c>$nor</c> mark
    /// the suggestion with a caveat that per-branch indexes may be more selective.
    /// </summary>
    private static readonly HashSet<string> AndCombinators = new(StringComparer.Ordinal) { "$and" };
    private static readonly HashSet<string> OrCombinators = new(StringComparer.Ordinal) { "$or", "$nor" };

    /// <summary>
    /// Attempts to build an index suggestion for the original command that produced a
    /// COLLSCAN. Returns <c>null</c> when nothing actionable can be extracted (empty filter,
    /// aggregate without a leading <c>$match</c>, command type without a filter, …).
    /// </summary>
    /// <param name="command">The original driver command (deep-cloned by the dispatcher).</param>
    /// <param name="commandName">Driver command name (<c>find</c>, <c>aggregate</c>, …).</param>
    /// <param name="target">Target collection name (used to build the shell command).</param>
    /// <param name="tenantId">
    /// Tenant database name; used together with <paramref name="ckCacheService"/> to look up
    /// the CK type graph for Stage 2D CK-YAML emission. Pass <c>null</c> to skip CK mapping
    /// and emit MongoDB-only suggestions (the Stage 2C shape).
    /// </param>
    /// <param name="ckCacheService">
    /// Loaded CK cache for the tenant. When supplied AND the filter carries a
    /// <c>ckTypeId.fullName</c> equality predicate, the suggester reverse-maps each
    /// MongoDB field path back to its CK attribute path and emits a CK-YAML snippet
    /// alongside the mongosh shell command (Stage 2D / AB#4222). When the cache is
    /// supplied but no resolvable type / attribute is found, the MongoDB-only suggestion
    /// is still returned — CK-YAML is additive, not gating.
    /// </param>
    public static SlowQueryIndexSuggestion? TrySuggest(
        BsonDocument? command,
        string commandName,
        string target,
        string? tenantId = null,
        ICkCacheService? ckCacheService = null)
    {
        if (command is null || command.ElementCount == 0 || string.IsNullOrEmpty(target))
        {
            return null;
        }

        try
        {
            var ctx = new SuggesterContext();
            var filter = ExtractFilter(command, commandName, ctx);
            if (filter is null)
            {
                return null;
            }

            Walk(filter, ctx, depth: 0);

            // Sort keys come from the command's top-level "sort" field. Adding them after
            // walking the filter lets us classify them as Sort (not Equality) even when the
            // same path also appeared in the filter.
            ExtractSort(command, ctx);

            if (ctx.Fields.Count == 0)
            {
                return null;
            }

            var ordered = OrderEsr(ctx.Fields);
            var indexName = BuildIndexName(ordered);
            var shellCommand = BuildShellCommand(target, ordered, indexName);
            var confidence = Rate(ordered, ctx);

            // Stage 2D — opportunistic CK-YAML emission. All four conditions must hold:
            //   1. caller supplied tenantId + CK cache
            //   2. filter carries a top-level ckTypeId.fullName equality (identifies the
            //      CK type the suggested index belongs to)
            //   3. that CK type is in the cache (could be a stale tenant or a type from a
            //      sibling tenant — silently skip)
            //   4. EVERY Mongo field reverse-maps to a CK attribute path against that type
            //      (partial mapping would mislead the operator, so it's all-or-nothing)
            string? ckYamlSnippet = null;
            string? ckTypeFullName = null;
            if (ckCacheService is not null && !string.IsNullOrEmpty(tenantId))
            {
                var maybeType = TryExtractCkTypeId(filter);
                if (maybeType is not null)
                {
                    ckYamlSnippet = TryBuildCkYamlSnippet(ckCacheService, tenantId, maybeType, ordered);
                    if (ckYamlSnippet is not null)
                    {
                        ckTypeFullName = maybeType;
                    }
                }
            }

            return new SlowQueryIndexSuggestion(
                IndexName: indexName,
                Fields: ordered,
                ShellCommand: shellCommand,
                Confidence: confidence,
                Notes: ctx.Notes,
                CkYamlSnippet: ckYamlSnippet,
                CkTypeFullName: ckTypeFullName);
        }
        catch (Exception ex)
        {
            // Suggester runs on the explain path which already runs on a background task —
            // failures here should leave the explain itself intact, not blow up the cache
            // entry. Surface the cause to the caller as a low-confidence stub with no fields,
            // but per the field-count guard above we'll return null and Stage 2C surface
            // shows no suggestion (which matches "we couldn't make sense of it").
            _ = ex;
            return null;
        }
    }

    /// <summary>
    /// Looks for a top-level <c>ckTypeId.fullName</c> equality predicate in the filter and
    /// returns the right-hand string value. We only walk the top level and the direct
    /// children of any <c>$and</c> branches — <c>$or</c>-mixed types deliberately fall
    /// through (we don't want to pick an arbitrary one and emit a CK-YAML snippet against
    /// the wrong type). Returns null when no unambiguous type can be derived.
    /// </summary>
    private static string? TryExtractCkTypeId(BsonDocument filter)
    {
        string? found = null;

        foreach (var element in filter.Elements)
        {
            // Equality at the top level: { "ckTypeId.fullName": "Demo/Asset" }
            if (element.Name == "ckTypeId.fullName" && element.Value.IsString)
            {
                if (found is not null && found != element.Value.AsString)
                {
                    // Two distinct equality predicates on the same path is degenerate; bail.
                    return null;
                }
                found = element.Value.AsString;
                continue;
            }

            // $and recurses; $or / $nor with mixed types deliberately doesn't.
            if (element.Name == "$and" && element.Value is BsonArray branches)
            {
                foreach (var branch in branches)
                {
                    if (branch is BsonDocument branchDoc)
                    {
                        var nested = TryExtractCkTypeId(branchDoc);
                        if (nested is null)
                        {
                            continue;
                        }
                        if (found is not null && found != nested)
                        {
                            return null;
                        }
                        found = nested;
                    }
                }
            }
        }

        return found;
    }

    /// <summary>
    /// Reverse-maps the suggestion's CK-attribute fields to their CK attribute paths and
    /// renders the YAML snippet. Returns null when the cache doesn't know the type, when
    /// no field is a CK attribute (the suggestion is purely non-attribute infra like
    /// <c>ckTypeId.fullName</c> + <c>rtState</c>), or when any field uses
    /// <c>Direction = -1</c> (CK indexes are always Ascending; mixing descending sort
    /// fields into a CK-YAML snippet would silently misrepresent the suggested index).
    /// </summary>
    /// <remarks>
    /// <para>
    /// Non-attribute fields (those <see cref="MongoDbAttributePathResolver.TryReverseToCkPath"/>
    /// rejects — <c>ckTypeId.fullName</c>, <c>rtState</c>, <c>_id</c>, etc.) are silently
    /// skipped rather than failing the whole snippet. They're filter discriminators or
    /// infra fields whose indexes either come from the CK system model or aren't
    /// representable in the per-type CK YAML at all. As long as AT LEAST ONE CK attribute
    /// resolves, a snippet ships listing those.
    /// </para>
    /// <para>
    /// The resulting CK YAML may therefore describe a STRICT SUBSET of the mongosh
    /// suggestion's fields. That's intentional: the CK-modelled index still helps the
    /// query (the type discriminator implicitly scopes it inside <c>rt_entities</c>) and
    /// is the right shape for a portable per-type model declaration. The Studio surface
    /// shows both so the operator can apply whichever is appropriate for their context.
    /// </para>
    /// </remarks>
    private static string? TryBuildCkYamlSnippet(
        ICkCacheService ckCacheService,
        string tenantId,
        string ckTypeFullName,
        IReadOnlyList<SlowQueryIndexField> fields)
    {
        // Descending direction can't be represented by Stage 2D's CK-YAML emission —
        // CkTypeIndexDto.IndexType only carries Ascending/Text/Unique/UniqueNotDeleted, no
        // per-field direction. Emitting Ascending when the suggestion was descending would
        // silently produce a different MongoDB index than the mongosh command. Bail rather
        // than mislead; the mongosh shell command still ships.
        foreach (var field in fields)
        {
            if (field.Direction == -1)
            {
                return null;
            }
        }

        // TryGetCkType: returns false (rather than throwing) for unknown / stale type IDs,
        // which is the right shape for an opportunistic enrichment path. The full-name
        // string is the constructor input for CkId<TElementId>, so a malformed value (e.g.
        // an empty discriminator we somehow read past the type-check) is caught here too.
        CkId<CkTypeId> ckTypeIdKey;
        try
        {
            ckTypeIdKey = new CkId<CkTypeId>(ckTypeFullName);
        }
        catch
        {
            return null;
        }

        if (!ckCacheService.TryGetCkType(tenantId, ckTypeIdKey, out var typeGraph) || typeGraph is null)
        {
            return null;
        }

        var provider = new CkCacheAttributeMetadataProvider(ckCacheService, tenantId, typeGraph);
        var ckPaths = new List<string>(fields.Count);
        foreach (var field in fields)
        {
            var ckPath = MongoDbAttributePathResolver.TryReverseToCkPath(field.Name, provider);
            if (ckPath is null)
            {
                // Non-attribute field (ckTypeId.fullName, rtState, _id, ...). Skip — these
                // are filter discriminators or infra fields, not paste-into-CK-source
                // material. The CK-YAML snippet lists the CK-attribute subset.
                continue;
            }
            ckPaths.Add(ckPath);
        }

        // Need at least one resolved CK attribute path to produce a meaningful snippet.
        // Zero-attribute suggestions (queries that only filter on type discriminator +
        // infra fields) emit only the mongosh command — there's no CK-YAML to write.
        if (ckPaths.Count == 0)
        {
            return null;
        }

        return CkYamlIndexSnippetWriter.Write(ckTypeFullName, ckPaths);
    }

    /// <summary>
    /// Locates the filter sub-document for the given command type. Returns null when the
    /// command shape doesn't carry an actionable filter — e.g. an aggregation whose first
    /// stage is not <c>$match</c>, or an explainable command type we don't recognise here.
    /// </summary>
    private static BsonDocument? ExtractFilter(BsonDocument command, string commandName, SuggesterContext ctx)
    {
        switch (commandName)
        {
            case "find":
            case "count":
                // find: {find: "<coll>", filter: {...}}; count: {count: "<coll>", query: {...}}
                return TryGetDocument(command, "filter") ?? TryGetDocument(command, "query");

            case "distinct":
                // {distinct: "<coll>", key: "fieldName", query: {...}}
                // The key field itself benefits from index coverage — append it as an equality
                // entry so the suggestion covers it even when the query is empty.
                if (command.TryGetValue("key", out var keyVal) && keyVal.IsString)
                {
                    ctx.AppendEquality(keyVal.AsString);
                }
                return TryGetDocument(command, "query");

            case "aggregate":
                return ExtractAggregateMatch(command, ctx);

            case "update":
            case "delete":
            case "findAndModify":
                // findAndModify uses "query"; update/delete pipelines use "updates"/"deletes"
                // arrays whose elements carry "q". The driver-level CommandStartedEvent we
                // receive flattens single-statement updates to the top level for legibility;
                // check both.
                if (command.TryGetValue("updates", out var updatesVal) && updatesVal is BsonArray updatesArr &&
                    updatesArr.Count > 0 && updatesArr[0] is BsonDocument firstUpdate)
                {
                    return TryGetDocument(firstUpdate, "q");
                }
                if (command.TryGetValue("deletes", out var deletesVal) && deletesVal is BsonArray deletesArr &&
                    deletesArr.Count > 0 && deletesArr[0] is BsonDocument firstDelete)
                {
                    return TryGetDocument(firstDelete, "q");
                }
                return TryGetDocument(command, "q") ?? TryGetDocument(command, "query");

            default:
                return null;
        }
    }

    /// <summary>
    /// Aggregations are only index-actionable when the FIRST pipeline stage is <c>$match</c> —
    /// downstream stages run against materialised intermediate sets that an index can't help.
    /// We deliberately don't walk past the first stage; doing so would emit indexes the planner
    /// would never use.
    /// </summary>
    private static BsonDocument? ExtractAggregateMatch(BsonDocument command, SuggesterContext ctx)
    {
        if (!command.TryGetValue("pipeline", out var pipelineVal) || pipelineVal is not BsonArray pipeline ||
            pipeline.Count == 0)
        {
            return null;
        }

        if (pipeline[0] is not BsonDocument firstStage)
        {
            return null;
        }

        if (!firstStage.TryGetValue("$match", out var matchVal) || matchVal is not BsonDocument matchDoc)
        {
            // Pipeline starts with something other than $match → no index will help. Some
            // operators (e.g. $search on Atlas) need their own tooling and we don't have
            // anything actionable.
            return null;
        }

        // Aggregates can also carry a top-level sort via `cursor.sort`; the more usual
        // pattern is a `$sort` stage right after `$match`. We honour the second-stage $sort
        // when it appears immediately after $match.
        if (pipeline.Count >= 2 && pipeline[1] is BsonDocument secondStage &&
            secondStage.TryGetValue("$sort", out var sortVal) && sortVal is BsonDocument sortDoc)
        {
            foreach (var element in sortDoc.Elements)
            {
                ctx.AppendSort(element.Name, ReadDirection(element.Value));
            }
        }

        return matchDoc;
    }

    private static BsonDocument? TryGetDocument(BsonDocument source, string elementName)
    {
        if (!source.TryGetValue(elementName, out var val) || val is not BsonDocument doc)
        {
            return null;
        }
        return doc;
    }

    /// <summary>
    /// Recursive filter walk. Each call inspects one filter document; nested documents reached
    /// via <c>$and</c>/<c>$or</c>/<c>$nor</c> recurse with an incremented depth counter.
    /// </summary>
    private static void Walk(BsonDocument filter, SuggesterContext ctx, int depth)
    {
        if (depth > MaxWalkDepth)
        {
            ctx.AddNote($"Filter exceeded {MaxWalkDepth} levels of nesting; deeper branches were not analysed.");
            return;
        }

        foreach (var element in filter.Elements)
        {
            var name = element.Name;

            if (AndCombinators.Contains(name))
            {
                RecurseCombinator(element.Value, ctx, depth, isOr: false);
                continue;
            }

            if (OrCombinators.Contains(name))
            {
                ctx.AddNote("Filter contains $or/$nor branches; per-branch indexes may be more selective than the suggested compound.");
                RecurseCombinator(element.Value, ctx, depth, isOr: true);
                continue;
            }

            if (name == "$not")
            {
                // $not wraps a single operator expression. Don't add a field here; the inner
                // shape will be picked up when it appears on a real field elsewhere. Note the
                // caveat.
                ctx.AddNote("Filter contains $not; the planner often refuses to use a regular index for negated predicates.");
                continue;
            }

            if (name.StartsWith('$'))
            {
                // Top-level operator that's neither a combinator nor $not. Special operators
                // (notably $text) are valid at the top level — e.g. {$text: {...}, tenantId: "t"}
                // mixes a fulltext predicate with a regular equality. The text-index caveat
                // belongs to the suggestion regardless of where $text appeared.
                if (SpecialOperators.TryGetValue(name, out var topLevelNote))
                {
                    ctx.MarkSpecialOperator(topLevelNote);
                }
                continue;
            }

            ClassifyField(name, element.Value, ctx);
        }
    }

    private static void RecurseCombinator(BsonValue value, SuggesterContext ctx, int depth, bool isOr)
    {
        if (value is not BsonArray branches)
        {
            return;
        }

        foreach (var branch in branches)
        {
            if (branch is BsonDocument branchDoc)
            {
                Walk(branchDoc, ctx, depth + 1);
            }
        }

        _ = isOr; // reserved for future per-branch logic; consumed by the AddNote call above
    }

    /// <summary>
    /// Reads the operator inside an operator document (<c>{a: {$gt: 5}}</c> → range on <c>a</c>)
    /// or treats the value as an equality scalar (<c>{a: 5}</c> → equality on <c>a</c>).
    /// </summary>
    private static void ClassifyField(string fieldName, BsonValue value, SuggesterContext ctx)
    {
        if (value is not BsonDocument operatorDoc)
        {
            // Scalar value or array literal → equality match.
            ctx.AppendEquality(fieldName);
            return;
        }

        // The operator document may contain multiple operators on the same field (e.g.
        // {a: {$gt: 0, $lt: 10}} — range on both sides). Classify by the strongest
        // implication encountered: equality > range > unknown.
        var sawEquality = false;
        var sawRange = false;
        var sawSpecial = false;

        foreach (var op in operatorDoc.Elements)
        {
            if (EqualityOperators.Contains(op.Name))
            {
                sawEquality = true;
                continue;
            }

            if (RangeOperators.Contains(op.Name))
            {
                sawRange = true;
                continue;
            }

            if (SpecialOperators.TryGetValue(op.Name, out var note))
            {
                ctx.MarkSpecialOperator(note);
                sawSpecial = true;
                continue;
            }

            // Unknown operator key on this field → ignore individually, but if no other
            // classification fires we'll fall back to equality so the field still appears in
            // the suggestion.
        }

        if (sawEquality)
        {
            ctx.AppendEquality(fieldName);
        }
        else if (sawRange)
        {
            ctx.AppendRange(fieldName);
        }
        else if (sawSpecial)
        {
            // Special ops already noted; still index the field so the operator gets a chance
            // to leverage it (e.g. $regex with anchored pattern).
            ctx.AppendEquality(fieldName);
        }
        else
        {
            // Defensive default — an opaque operator doc on a field we still want indexed.
            ctx.AppendEquality(fieldName);
        }
    }

    /// <summary>
    /// Pulls the command's <c>sort</c> field (when present) and merges into the context as
    /// sort entries. Re-classifies any equality/range field that also appears in sort as a
    /// sort key, so it lands in the middle position per ESR rather than the front.
    /// </summary>
    private static void ExtractSort(BsonDocument command, SuggesterContext ctx)
    {
        if (!command.TryGetValue("sort", out var sortVal) || sortVal is not BsonDocument sortDoc)
        {
            return;
        }

        foreach (var element in sortDoc.Elements)
        {
            ctx.AppendSort(element.Name, ReadDirection(element.Value));
        }
    }

    private static int ReadDirection(BsonValue value) => value switch
    {
        BsonInt32 i32 when i32.Value < 0 => -1,
        BsonInt64 i64 when i64.Value < 0 => -1,
        BsonDouble d when d.Value < 0 => -1,
        _ => 1
    };

    /// <summary>
    /// Sorts the collected fields per Mongo's ESR rule: Equality → Sort → Range. Within each
    /// category, the original insertion order is preserved so an operator can reason about
    /// "first equality field" deterministically.
    /// </summary>
    private static IReadOnlyList<SlowQueryIndexField> OrderEsr(IReadOnlyDictionary<string, FieldEntry> fields)
    {
        var ordered = fields.Values
            .OrderBy(e => Priority(e.Kind))
            .ThenBy(e => e.Order)
            .Select(e => new SlowQueryIndexField(e.Name, e.Direction, e.Kind))
            .ToList();
        return ordered;

        static int Priority(SlowQueryIndexFieldKind kind) => kind switch
        {
            SlowQueryIndexFieldKind.Equality => 0,
            SlowQueryIndexFieldKind.Sort => 1,
            SlowQueryIndexFieldKind.Range => 2,
            _ => 3
        };
    }

    /// <summary>
    /// Builds the index name per Mongo's convention: <c>field_direction[_field_direction]…</c>.
    /// Names that would exceed the 127-byte limit are truncated and suffixed with a SHA-256
    /// short hash so similar shapes don't collide.
    /// </summary>
    private static string BuildIndexName(IReadOnlyList<SlowQueryIndexField> fields)
    {
        var raw = string.Join("_",
            fields.Select(f => f.Name.Replace('.', '_') + "_" + f.Direction));

        if (Encoding.UTF8.GetByteCount(raw) <= MaxIndexNameBytes)
        {
            return raw;
        }

        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(raw)))
            .Substring(0, 8)
            .ToLowerInvariant();
        var suffix = "_" + hash;

        // Truncate by characters and re-measure until we fit, then append the suffix.
        var span = raw.AsSpan();
        var budget = MaxIndexNameBytes - Encoding.UTF8.GetByteCount(suffix);
        var accumulated = 0;
        var charsConsumed = 0;
        foreach (var rune in span.EnumerateRunes())
        {
            var runeBytes = rune.Utf8SequenceLength;
            if (accumulated + runeBytes > budget)
            {
                break;
            }
            accumulated += runeBytes;
            charsConsumed += rune.Utf16SequenceLength;
        }

        return raw.Substring(0, charsConsumed) + suffix;
    }

    /// <summary>
    /// Builds the mongosh shell command. Field paths are JSON-string-quoted because dotted
    /// paths (the OctoMesh norm: <c>attributes.name.value</c>) are not valid JS identifiers
    /// and must be quoted in the spec literal. We escape backslashes and double quotes inside
    /// the field/index-name literals — defence in depth against pathologically named fields
    /// or generated index names that contain those characters; without escaping a name like
    /// <c>a"b</c> would close the surrounding quote and produce broken mongosh.
    /// </summary>
    private static string BuildShellCommand(
        string target,
        IReadOnlyList<SlowQueryIndexField> fields,
        string indexName)
    {
        var spec = string.Join(", ",
            fields.Select(f => $"\"{EscapeForJsString(f.Name)}\": {f.Direction}"));
        return $"db.{target}.createIndex({{{spec}}}, {{name: \"{EscapeForJsString(indexName)}\"}})";
    }

    /// <summary>
    /// Escapes backslashes and double quotes for inclusion inside a JavaScript double-quoted
    /// string literal. Order matters — backslash first, otherwise the just-inserted escape
    /// itself gets escaped again on the second pass.
    /// </summary>
    private static string EscapeForJsString(string value)
        => value.Replace("\\", "\\\\").Replace("\"", "\\\"");

    /// <summary>
    /// Confidence heuristic — see <see cref="SlowQueryIndexSuggestionConfidence"/> XML doc.
    /// A note about $or/$nor or any special operator downgrades to Low regardless of field
    /// count, because the suggested compound may not be what the planner picks.
    /// </summary>
    private static SlowQueryIndexSuggestionConfidence Rate(
        IReadOnlyList<SlowQueryIndexField> fields, SuggesterContext ctx)
    {
        if (ctx.HasOrBranches || ctx.HasSpecialOperator || fields.Count >= 4)
        {
            return SlowQueryIndexSuggestionConfidence.Low;
        }

        if (fields.Count == 1 && fields[0].Kind == SlowQueryIndexFieldKind.Equality)
        {
            return SlowQueryIndexSuggestionConfidence.High;
        }

        return SlowQueryIndexSuggestionConfidence.Medium;
    }

    /// <summary>
    /// Aggregator state for one suggest call. Tracks insertion order so the ESR sort is
    /// stable, dedups by field name (last write wins on kind), and collects Notes that we
    /// surface in the suggestion. Not thread-safe — one instance per call.
    /// </summary>
    private sealed class SuggesterContext
    {
        public Dictionary<string, FieldEntry> Fields { get; } = new(StringComparer.Ordinal);
        public List<string> Notes { get; } = new();
        public bool HasOrBranches { get; private set; }
        public bool HasSpecialOperator { get; private set; }
        private int _orderCounter;

        public void AppendEquality(string name) => Append(name, 1, SlowQueryIndexFieldKind.Equality);
        public void AppendRange(string name) => Append(name, 1, SlowQueryIndexFieldKind.Range);
        public void AppendSort(string name, int direction) => Append(name, direction, SlowQueryIndexFieldKind.Sort);

        private void Append(string name, int direction, SlowQueryIndexFieldKind kind)
        {
            if (string.IsNullOrEmpty(name))
            {
                return;
            }

            if (Fields.TryGetValue(name, out var existing))
            {
                // Promotion order: any non-Sort kind seen later as a Sort upgrades to Sort —
                // both Equality + Sort AND Range + Sort patterns are common (a range
                // predicate plus an ordered output of the same field). In either case the
                // Order is re-stamped to the sort document's position; otherwise a filter
                // walk that inserted the field first would force a compound order the
                // planner can't use to satisfy the sort, and the index is wasted on an
                // in-memory sort step.
                if (kind == SlowQueryIndexFieldKind.Sort && existing.Kind != SlowQueryIndexFieldKind.Sort)
                {
                    Fields[name] = existing with
                    {
                        Kind = SlowQueryIndexFieldKind.Sort,
                        Direction = direction,
                        Order = _orderCounter++
                    };
                }
                else if (kind == SlowQueryIndexFieldKind.Equality && existing.Kind == SlowQueryIndexFieldKind.Range)
                {
                    Fields[name] = existing with { Kind = SlowQueryIndexFieldKind.Equality, Direction = 1 };
                }

                return;
            }

            Fields[name] = new FieldEntry(name, direction, kind, _orderCounter++);
        }

        public void AddNote(string note)
        {
            if (!Notes.Contains(note, StringComparer.Ordinal))
            {
                Notes.Add(note);
            }

            if (note.StartsWith("Filter contains $or", StringComparison.Ordinal))
            {
                HasOrBranches = true;
            }
        }

        /// <summary>
        /// Flags a special-operator hit and records its caveat in one call. The flag is set
        /// explicitly here rather than inferred from the note's text — earlier we tried
        /// inferring via "operator detected" substring match, which silently broke for
        /// $regex / $elemMatch notes that didn't contain that exact phrase, and would have
        /// broken again on any future note rewording.
        /// </summary>
        public void MarkSpecialOperator(string note)
        {
            AddNote(note);
            HasSpecialOperator = true;
        }
    }

    private sealed record FieldEntry(string Name, int Direction, SlowQueryIndexFieldKind Kind, int Order);
}
