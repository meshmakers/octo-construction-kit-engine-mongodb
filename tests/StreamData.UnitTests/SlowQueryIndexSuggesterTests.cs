using FakeItEasy;

using Meshmakers.Octo.Runtime.Engine.MongoDb.Repositories.MongoDb.Generic;

using MongoDB.Bson;

namespace Meshmakers.Octo.Runtime.Engine.UnitTests;

public class SlowQueryIndexSuggesterTests
{
    // ---- null / empty short-circuits ------------------------------------------------------

    [Fact]
    public void TrySuggest_NullCommand_ReturnsNull()
    {
        Assert.Null(SlowQueryIndexSuggester.TrySuggest(null, "find", "rt_entities"));
    }

    [Fact]
    public void TrySuggest_EmptyCommand_ReturnsNull()
    {
        Assert.Null(SlowQueryIndexSuggester.TrySuggest(new BsonDocument(), "find", "rt_entities"));
    }

    [Fact]
    public void TrySuggest_EmptyTarget_ReturnsNull()
    {
        var cmd = new BsonDocument { { "find", "rt_entities" }, { "filter", new BsonDocument("a", 1) } };
        Assert.Null(SlowQueryIndexSuggester.TrySuggest(cmd, "find", string.Empty));
    }

    [Fact]
    public void TrySuggest_FindWithEmptyFilter_ReturnsNull()
    {
        var cmd = new BsonDocument { { "find", "rt_entities" }, { "filter", new BsonDocument() } };
        Assert.Null(SlowQueryIndexSuggester.TrySuggest(cmd, "find", "rt_entities"));
    }

    [Fact]
    public void TrySuggest_UnknownCommandType_ReturnsNull()
    {
        var cmd = new BsonDocument { { "insert", "rt_entities" } };
        Assert.Null(SlowQueryIndexSuggester.TrySuggest(cmd, "insert", "rt_entities"));
    }

    // ---- find: equality / range / ESR / nested paths -------------------------------------

    [Fact]
    public void TrySuggest_FindWithSingleEquality_HighConfidence_AscendingDirection()
    {
        // The classic missing-index case: a single equality predicate. Confidence is high
        // because exactly one column appears, no $or, no special operators.
        var cmd = new BsonDocument
        {
            { "find", "rt_entities" },
            { "filter", new BsonDocument("ckTypeId.fullName", "Demo/Asset") }
        };

        var s = SlowQueryIndexSuggester.TrySuggest(cmd, "find", "rt_entities")!;

        Assert.NotNull(s);
        Assert.Equal(SlowQueryIndexSuggestionConfidence.High, s.Confidence);
        var field = Assert.Single(s.Fields);
        Assert.Equal("ckTypeId.fullName", field.Name);
        Assert.Equal(1, field.Direction);
        Assert.Equal(SlowQueryIndexFieldKind.Equality, field.Kind);
        Assert.Empty(s.Notes);
    }

    [Fact]
    public void TrySuggest_FindWithEqualityAndRange_AppliesEsrOrdering()
    {
        // ESR rule: a (equality) MUST appear before b (range). Even though the BSON element
        // order is a-then-b, the generator must reorder them when range comes first in the
        // BSON.
        var cmd = new BsonDocument
        {
            { "find", "rt_entities" },
            { "filter", new BsonDocument
                {
                    { "createdAt", new BsonDocument("$gt", DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()) }, // range
                    { "ckTypeId.fullName", "Demo/Asset" } // equality
                }
            }
        };

        var s = SlowQueryIndexSuggester.TrySuggest(cmd, "find", "rt_entities")!;

        Assert.Equal(2, s.Fields.Count);
        // Equality first
        Assert.Equal("ckTypeId.fullName", s.Fields[0].Name);
        Assert.Equal(SlowQueryIndexFieldKind.Equality, s.Fields[0].Kind);
        // Range last
        Assert.Equal("createdAt", s.Fields[1].Name);
        Assert.Equal(SlowQueryIndexFieldKind.Range, s.Fields[1].Kind);
        Assert.Equal(SlowQueryIndexSuggestionConfidence.Medium, s.Confidence);
    }

    [Fact]
    public void TrySuggest_FindWithSort_AppliesEsrOrdering_SortBetweenEqualityAndRange()
    {
        // Equality on a, sort on b ascending, range on c → ESR order: a (Eq), b (Sort), c (Range).
        var cmd = new BsonDocument
        {
            { "find", "rt_entities" },
            { "filter", new BsonDocument
                {
                    { "a", 1 },
                    { "c", new BsonDocument("$gt", 5) }
                }
            },
            { "sort", new BsonDocument("b", -1) }
        };

        var s = SlowQueryIndexSuggester.TrySuggest(cmd, "find", "rt_entities")!;

        Assert.Equal(3, s.Fields.Count);
        Assert.Equal("a", s.Fields[0].Name);
        Assert.Equal(SlowQueryIndexFieldKind.Equality, s.Fields[0].Kind);
        Assert.Equal("b", s.Fields[1].Name);
        Assert.Equal(SlowQueryIndexFieldKind.Sort, s.Fields[1].Kind);
        Assert.Equal(-1, s.Fields[1].Direction);
        Assert.Equal("c", s.Fields[2].Name);
        Assert.Equal(SlowQueryIndexFieldKind.Range, s.Fields[2].Kind);
    }

    [Fact]
    public void TrySuggest_FindWithDottedFieldPath_PreservedVerbatim()
    {
        var cmd = new BsonDocument
        {
            { "find", "rt_entities" },
            { "filter", new BsonDocument("attributes.name.value", "Salzburg") }
        };

        var s = SlowQueryIndexSuggester.TrySuggest(cmd, "find", "rt_entities")!;

        var field = Assert.Single(s.Fields);
        Assert.Equal("attributes.name.value", field.Name);
        Assert.Contains("\"attributes.name.value\": 1", s.ShellCommand);
    }

    [Fact]
    public void TrySuggest_OperatorKeysNotTreatedAsFieldPaths()
    {
        // {a: {$gt: 0, $lt: 10}} → range on `a` (NOT separate $gt / $lt fields).
        // {b: {$in: [1, 2]}}   → equality on `b` ($in is equality-class).
        var cmd = new BsonDocument
        {
            { "find", "x" },
            { "filter", new BsonDocument
                {
                    { "a", new BsonDocument { { "$gt", 0 }, { "$lt", 10 } } },
                    { "b", new BsonDocument("$in", new BsonArray { 1, 2 }) }
                }
            }
        };

        var s = SlowQueryIndexSuggester.TrySuggest(cmd, "find", "x")!;

        Assert.Equal(2, s.Fields.Count);
        Assert.DoesNotContain(s.Fields, f => f.Name.StartsWith("$"));
        Assert.Contains(s.Fields, f => f.Name == "a" && f.Kind == SlowQueryIndexFieldKind.Range);
        Assert.Contains(s.Fields, f => f.Name == "b" && f.Kind == SlowQueryIndexFieldKind.Equality);
    }

    // ---- $and / $or / $nor branch walking -----------------------------------------------

    [Fact]
    public void TrySuggest_FindWithAndBranches_UnionsFields_NoCaveat()
    {
        var cmd = new BsonDocument
        {
            { "find", "x" },
            { "filter", new BsonDocument("$and", new BsonArray
                {
                    new BsonDocument("a", 1),
                    new BsonDocument("b", 2)
                })
            }
        };

        var s = SlowQueryIndexSuggester.TrySuggest(cmd, "find", "x")!;

        Assert.Equal(2, s.Fields.Count);
        Assert.Contains(s.Fields, f => f.Name == "a");
        Assert.Contains(s.Fields, f => f.Name == "b");
        Assert.DoesNotContain(s.Notes, n => n.Contains("$or"));
    }

    [Fact]
    public void TrySuggest_FindWithOrBranches_AddsCaveat_AndLowConfidence()
    {
        var cmd = new BsonDocument
        {
            { "find", "x" },
            { "filter", new BsonDocument("$or", new BsonArray
                {
                    new BsonDocument("a", 1),
                    new BsonDocument("b", 2)
                })
            }
        };

        var s = SlowQueryIndexSuggester.TrySuggest(cmd, "find", "x")!;

        Assert.Equal(2, s.Fields.Count);
        Assert.Contains(s.Notes, n => n.Contains("$or"));
        Assert.Equal(SlowQueryIndexSuggestionConfidence.Low, s.Confidence);
    }

    // ---- special operators ($text / $near / $regex / $elemMatch) -----------------------

    [Fact]
    public void TrySuggest_TextOperator_FlagsNote_AndDowngradesConfidence()
    {
        var cmd = new BsonDocument
        {
            { "find", "docs" },
            { "filter", new BsonDocument("description", new BsonDocument("$text", "hello")) }
        };

        var s = SlowQueryIndexSuggester.TrySuggest(cmd, "find", "docs")!;

        Assert.Contains(s.Notes, n => n.Contains("text"));
        Assert.Equal(SlowQueryIndexSuggestionConfidence.Low, s.Confidence);
        Assert.Single(s.Fields, f => f.Name == "description");
    }

    [Fact]
    public void TrySuggest_GeoNear_FlagsGeospatialNote()
    {
        var cmd = new BsonDocument
        {
            { "find", "places" },
            { "filter", new BsonDocument("location", new BsonDocument("$near", new BsonArray { 0, 0 })) }
        };

        var s = SlowQueryIndexSuggester.TrySuggest(cmd, "find", "places")!;

        Assert.Contains(s.Notes, n => n.Contains("geospatial"));
    }

    [Fact]
    public void TrySuggest_ElemMatch_FlagsMultikeyNote()
    {
        var cmd = new BsonDocument
        {
            { "find", "x" },
            { "filter", new BsonDocument("tags", new BsonDocument("$elemMatch",
                new BsonDocument("$eq", "blue"))) }
        };

        var s = SlowQueryIndexSuggester.TrySuggest(cmd, "find", "x")!;

        Assert.Contains(s.Notes, n => n.Contains("multikey", StringComparison.OrdinalIgnoreCase));
    }

    // ---- aggregate ---------------------------------------------------------------------

    [Fact]
    public void TrySuggest_AggregateWithLeadingMatch_ExtractsFields()
    {
        var cmd = new BsonDocument
        {
            { "aggregate", "rt_entities" },
            { "pipeline", new BsonArray
                {
                    new BsonDocument("$match", new BsonDocument("ckTypeId.fullName", "Demo/Asset")),
                    new BsonDocument("$group", new BsonDocument("_id", "$ckTypeId.fullName"))
                }
            }
        };

        var s = SlowQueryIndexSuggester.TrySuggest(cmd, "aggregate", "rt_entities")!;

        Assert.NotNull(s);
        Assert.Single(s.Fields, f => f.Name == "ckTypeId.fullName");
    }

    [Fact]
    public void TrySuggest_AggregateWithoutLeadingMatch_ReturnsNull()
    {
        // No $match → no index will help.
        var cmd = new BsonDocument
        {
            { "aggregate", "rt_entities" },
            { "pipeline", new BsonArray
                {
                    new BsonDocument("$group", new BsonDocument("_id", "$ckTypeId.fullName"))
                }
            }
        };

        Assert.Null(SlowQueryIndexSuggester.TrySuggest(cmd, "aggregate", "rt_entities"));
    }

    [Fact]
    public void TrySuggest_AggregateMatchPlusSort_HonorsBothStages()
    {
        // $match on tenantId (equality), $sort on timestamp DESC → ESR order: tenantId (Eq), timestamp (Sort).
        var cmd = new BsonDocument
        {
            { "aggregate", "rt_entities" },
            { "pipeline", new BsonArray
                {
                    new BsonDocument("$match", new BsonDocument("tenantId", "t_a")),
                    new BsonDocument("$sort", new BsonDocument("timestamp", -1))
                }
            }
        };

        var s = SlowQueryIndexSuggester.TrySuggest(cmd, "aggregate", "rt_entities")!;

        Assert.Equal(2, s.Fields.Count);
        Assert.Equal("tenantId", s.Fields[0].Name);
        Assert.Equal(SlowQueryIndexFieldKind.Equality, s.Fields[0].Kind);
        Assert.Equal("timestamp", s.Fields[1].Name);
        Assert.Equal(SlowQueryIndexFieldKind.Sort, s.Fields[1].Kind);
        Assert.Equal(-1, s.Fields[1].Direction);
    }

    // ---- count / distinct / update / delete --------------------------------------------

    [Fact]
    public void TrySuggest_Count_TreatedAsFindFilter()
    {
        var cmd = new BsonDocument
        {
            { "count", "x" },
            { "query", new BsonDocument("a", 1) }
        };

        var s = SlowQueryIndexSuggester.TrySuggest(cmd, "count", "x")!;

        Assert.Single(s.Fields, f => f.Name == "a");
    }

    [Fact]
    public void TrySuggest_Distinct_AppendsKeyFieldAsEquality()
    {
        // distinct's "key" field benefits from index coverage even when query is empty.
        var cmd = new BsonDocument
        {
            { "distinct", "x" },
            { "key", "tenantId" },
            { "query", new BsonDocument() }
        };

        var s = SlowQueryIndexSuggester.TrySuggest(cmd, "distinct", "x")!;

        Assert.Single(s.Fields, f => f.Name == "tenantId" && f.Kind == SlowQueryIndexFieldKind.Equality);
    }

    [Fact]
    public void TrySuggest_Update_ReadsQFilter()
    {
        // Multi-statement update wraps its q inside "updates"[0].q
        var cmd = new BsonDocument
        {
            { "update", "x" },
            { "updates", new BsonArray
                {
                    new BsonDocument
                    {
                        { "q", new BsonDocument("ckTypeId.fullName", "Demo/Asset") },
                        { "u", new BsonDocument("$set", new BsonDocument("status", "archived")) }
                    }
                }
            }
        };

        var s = SlowQueryIndexSuggester.TrySuggest(cmd, "update", "x")!;

        Assert.Single(s.Fields, f => f.Name == "ckTypeId.fullName");
    }

    [Fact]
    public void TrySuggest_Delete_ReadsQFilter()
    {
        var cmd = new BsonDocument
        {
            { "delete", "x" },
            { "deletes", new BsonArray
                {
                    new BsonDocument { { "q", new BsonDocument("a", 1) }, { "limit", 1 } }
                }
            }
        };

        var s = SlowQueryIndexSuggester.TrySuggest(cmd, "delete", "x")!;

        Assert.Single(s.Fields, f => f.Name == "a");
    }

    // ---- shell-command formatting ------------------------------------------------------

    [Fact]
    public void TrySuggest_ShellCommand_QuotesDottedPaths_AndIncludesNameOption()
    {
        var cmd = new BsonDocument
        {
            { "find", "rt_entities" },
            { "filter", new BsonDocument("attributes.name.value", "x") }
        };

        var s = SlowQueryIndexSuggester.TrySuggest(cmd, "find", "rt_entities")!;

        Assert.StartsWith("db.rt_entities.createIndex(", s.ShellCommand);
        Assert.Contains("\"attributes.name.value\": 1", s.ShellCommand);
        Assert.Contains("name: \"", s.ShellCommand);
    }

    [Fact]
    public void TrySuggest_IndexName_StaysUnder127Bytes_EvenForLongPaths()
    {
        // Build a deliberately huge field path to push the generated name over the limit.
        var longPath = "attributes." + new string('a', 200) + ".value";
        var cmd = new BsonDocument
        {
            { "find", "x" },
            { "filter", new BsonDocument(longPath, "y") }
        };

        var s = SlowQueryIndexSuggester.TrySuggest(cmd, "find", "x")!;

        Assert.True(System.Text.Encoding.UTF8.GetByteCount(s.IndexName) <= 127,
            $"Index name was {System.Text.Encoding.UTF8.GetByteCount(s.IndexName)} bytes: {s.IndexName}");
        // Truncated names get the 8-char hash suffix so collisions across similar shapes
        // stay distinct.
        Assert.Matches(@"_[0-9a-f]{8}$", s.IndexName);
    }

    // ---- confidence ratings ------------------------------------------------------------

    // ---- review fixes (PR #105) ---------------------------------------------------------

    [Fact]
    public void TrySuggest_ShellCommand_EscapesQuoteAndBackslashInFieldName()
    {
        // Pathologically named field with embedded backslash and double quote. Without
        // escaping, the emitted createIndex literal would be syntactically broken (and a
        // path to inject if untrusted input ever shapes field names). The escape pass is
        // defence in depth: we don't expect this in OctoMesh today, but the suggester is
        // generic over the BSON shape.
        var cmd = new BsonDocument
        {
            { "find", "x" },
            { "filter", new BsonDocument("evil\\name\"with quote", 1) }
        };

        var s = SlowQueryIndexSuggester.TrySuggest(cmd, "find", "x")!;

        Assert.Contains("\\\\", s.ShellCommand); // backslash → \\
        Assert.Contains("\\\"", s.ShellCommand); // double-quote → \"
        // The literal quote-without-escape inside the field name must NOT appear unescaped.
        // We look specifically for the substring `name"with` (raw quote between letters); if
        // escaping ever broke, that's the substring that would show up.
        Assert.DoesNotContain("name\"with", s.ShellCommand);
    }

    [Fact]
    public void TrySuggest_TopLevelTextOperator_FlagsCaveatEvenWithRegularFields()
    {
        // {$text: {...}, tenantId: "t"} — $text at the TOP level, not inside a field doc.
        // Walk() must still record the text-index caveat when this shape appears, otherwise
        // the operator picks up a regular compound index suggestion that the planner can't
        // use for the fulltext predicate.
        var cmd = new BsonDocument
        {
            { "find", "docs" },
            { "filter", new BsonDocument
                {
                    { "$text", new BsonDocument("$search", "needle") },
                    { "tenantId", "t" }
                }
            }
        };

        var s = SlowQueryIndexSuggester.TrySuggest(cmd, "find", "docs")!;

        Assert.Contains(s.Notes, n => n.Contains("text", StringComparison.OrdinalIgnoreCase));
        Assert.Equal(SlowQueryIndexSuggestionConfidence.Low, s.Confidence);
    }

    [Fact]
    public void TrySuggest_RegexOperator_DowngradesConfidenceToLow()
    {
        // The regex note is "$regex detected — index is only used for anchored patterns".
        // Previously HasSpecialOperator was inferred via the substring "operator detected",
        // which wouldn't match this note's text — letting a single-regex-field query slip
        // through as High confidence. MarkSpecialOperator sets the flag explicitly.
        var cmd = new BsonDocument
        {
            { "find", "x" },
            { "filter", new BsonDocument("name", new BsonDocument("$regex", "^prefix")) }
        };

        var s = SlowQueryIndexSuggester.TrySuggest(cmd, "find", "x")!;

        Assert.Contains(s.Notes, n => n.Contains("$regex"));
        Assert.Equal(SlowQueryIndexSuggestionConfidence.Low, s.Confidence);
    }

    [Fact]
    public void TrySuggest_ElemMatchOperator_DowngradesConfidenceToLow()
    {
        var cmd = new BsonDocument
        {
            { "find", "x" },
            { "filter", new BsonDocument("tags", new BsonDocument("$elemMatch",
                new BsonDocument("$eq", "blue"))) }
        };

        var s = SlowQueryIndexSuggester.TrySuggest(cmd, "find", "x")!;

        Assert.Contains(s.Notes, n => n.Contains("$elemMatch"));
        Assert.Equal(SlowQueryIndexSuggestionConfidence.Low, s.Confidence);
    }

    [Fact]
    public void TrySuggest_SortReordersFiltersToMatchSortDocument()
    {
        // Both a and b appear in the filter; the sort document specifies b BEFORE a. The
        // resulting compound MUST be {b, a} — otherwise the planner can't use it to
        // satisfy the sort without a separate in-memory sort step. Sort field order from
        // the sort document overrides filter-walk insertion order.
        var cmd = new BsonDocument
        {
            { "find", "x" },
            { "filter", new BsonDocument
                {
                    { "a", new BsonDocument("$exists", true) }, // range-ish, eligible for sort upgrade
                    { "b", new BsonDocument("$exists", true) }
                }
            },
            { "sort", new BsonDocument { { "b", 1 }, { "a", 1 } } }
        };

        var s = SlowQueryIndexSuggester.TrySuggest(cmd, "find", "x")!;

        Assert.Equal(2, s.Fields.Count);
        // After the sort-driven re-stamp, b must come first.
        Assert.Equal("b", s.Fields[0].Name);
        Assert.Equal("a", s.Fields[1].Name);
        Assert.All(s.Fields, f => Assert.Equal(SlowQueryIndexFieldKind.Sort, f.Kind));
    }

    // ---- Stage 2D CK-YAML suggester wiring (AB#4222) ------------------------------------

    [Fact]
    public void TrySuggest_WithoutCkCache_NoCkYamlSnippet()
    {
        // Regression test for Stage 2C behaviour: when no CK cache is wired (legacy callers,
        // hosts without the construction-kit engine attached), the suggester still emits
        // the MongoDB-only suggestion with no CK-YAML snippet.
        var cmd = new BsonDocument
        {
            { "find", "rt_entities" },
            { "filter", new BsonDocument
                {
                    { "ckTypeId.fullName", "Demo/Asset" },
                    { "attributes.name.value", "x" }
                }
            }
        };

        var s = SlowQueryIndexSuggester.TrySuggest(cmd, "find", "rt_entities");

        Assert.NotNull(s);
        Assert.Null(s.CkYamlSnippet);
        Assert.Null(s.CkTypeFullName);
        Assert.NotEmpty(s.ShellCommand); // Stage 2C suggestion still ships
    }

    [Fact]
    public void TrySuggest_WithCkCacheButNoTypeFilter_NoCkYamlSnippet()
    {
        // Filter has the slow path's attribute fields but no ckTypeId.fullName equality.
        // We can't safely attribute the index to a single CK type, so CK-YAML stays null.
        var fakeCache = A.Fake<Meshmakers.Octo.ConstructionKit.Contracts.Services.ICkCacheService>();
        var cmd = new BsonDocument
        {
            { "find", "rt_entities" },
            { "filter", new BsonDocument("attributes.name.value", "x") }
        };

        var s = SlowQueryIndexSuggester.TrySuggest(cmd, "find", "rt_entities",
            tenantId: "tenant_a", ckCacheService: fakeCache)!;

        Assert.NotNull(s);
        Assert.Null(s.CkYamlSnippet);
        Assert.Null(s.CkTypeFullName);
    }

    [Fact]
    public void TrySuggest_WithCkCacheAndTypeFilter_TypeUnknown_NoCkYamlSnippet()
    {
        // The filter carries ckTypeId.fullName, but the cache doesn't know that type
        // (stale tenant, sibling-tenant type, typo). TryGetCkType returns false; suggester
        // bails on CK-YAML, MongoDB-only ships.
        var fakeCache = A.Fake<Meshmakers.Octo.ConstructionKit.Contracts.Services.ICkCacheService>();
        Meshmakers.Octo.ConstructionKit.Contracts.DependencyGraph.CkTypeGraph? outGraph = null;
        A.CallTo(() => fakeCache.TryGetCkType(
            A<string>._,
            A<Meshmakers.Octo.ConstructionKit.Contracts.CkId<Meshmakers.Octo.ConstructionKit.Contracts.CkTypeId>>._,
            out outGraph))
            .Returns(false);

        var cmd = new BsonDocument
        {
            { "find", "rt_entities" },
            { "filter", new BsonDocument
                {
                    { "ckTypeId.fullName", "Demo/Unknown" },
                    { "attributes.name.value", "x" }
                }
            }
        };

        var s = SlowQueryIndexSuggester.TrySuggest(cmd, "find", "rt_entities",
            tenantId: "tenant_a", ckCacheService: fakeCache)!;

        Assert.NotNull(s);
        Assert.Null(s.CkYamlSnippet);
        Assert.Null(s.CkTypeFullName);
        Assert.NotEmpty(s.ShellCommand);
    }

    [Fact]
    public void TrySuggest_WithCkCacheAndOrBranchedTypes_NoCkYamlSnippet()
    {
        // {$or: [{ckTypeId.fullName: A}, {ckTypeId.fullName: B}]} — two distinct types in
        // an $or, we don't know which to attribute the index to. TryExtractCkTypeId returns
        // null when the equality value can't be uniquely determined.
        // We don't even need to set up TryGetCkType — the type extraction short-circuits.
        var fakeCache = A.Fake<Meshmakers.Octo.ConstructionKit.Contracts.Services.ICkCacheService>();
        var cmd = new BsonDocument
        {
            { "find", "rt_entities" },
            { "filter", new BsonDocument("$or", new BsonArray
                {
                    new BsonDocument("ckTypeId.fullName", "Demo/A"),
                    new BsonDocument("ckTypeId.fullName", "Demo/B")
                })
            }
        };

        var s = SlowQueryIndexSuggester.TrySuggest(cmd, "find", "rt_entities",
            tenantId: "tenant_a", ckCacheService: fakeCache)!;

        Assert.NotNull(s);
        Assert.Null(s.CkYamlSnippet);
        Assert.Null(s.CkTypeFullName);
    }

    [Fact]
    public void Confidence_Low_When_ManyFields()
    {
        var cmd = new BsonDocument
        {
            { "find", "x" },
            { "filter", new BsonDocument
                {
                    { "a", 1 }, { "b", 2 }, { "c", 3 }, { "d", 4 }
                }
            }
        };

        var s = SlowQueryIndexSuggester.TrySuggest(cmd, "find", "x")!;

        Assert.Equal(SlowQueryIndexSuggestionConfidence.Low, s.Confidence);
    }
}
