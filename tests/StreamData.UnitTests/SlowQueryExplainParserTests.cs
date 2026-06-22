using Meshmakers.Octo.Runtime.Engine.MongoDb.Repositories.MongoDb.Generic;

using MongoDB.Bson;

namespace Meshmakers.Octo.Runtime.Engine.UnitTests;

public class SlowQueryExplainParserTests
{
    private static readonly DateTimeOffset Now = new(2026, 6, 22, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void Parse_NullInput_ReturnsFailed()
    {
        var result = SlowQueryExplainParser.Parse(null, Now, rawPreviewBytes: 0);

        Assert.Equal(SlowQueryExplainStatus.Failed, result.Status);
        Assert.Contains("null", result.ErrorMessage ?? string.Empty);
        Assert.False(result.HasCollScan);
        Assert.Empty(result.IndexNames);
    }

    [Fact]
    public void Parse_MissingQueryPlanner_ReturnsFailed()
    {
        var malformed = new BsonDocument { { "ok", 1 } };

        var result = SlowQueryExplainParser.Parse(malformed, Now, rawPreviewBytes: 0);

        Assert.Equal(SlowQueryExplainStatus.Failed, result.Status);
        Assert.Contains("queryPlanner", result.ErrorMessage ?? string.Empty);
    }

    [Fact]
    public void Parse_FindCollScan_FlagsCollScanAndCapturesStage()
    {
        // Shape produced by db.runCommand({explain: {find: ..., filter: ...}, verbosity: "queryPlanner"})
        // against a collection with no usable index.
        var explain = new BsonDocument
        {
            { "queryPlanner", new BsonDocument
                {
                    { "winningPlan", new BsonDocument
                        {
                            { "stage", "COLLSCAN" },
                            { "direction", "forward" }
                        }
                    }
                }
            }
        };

        var result = SlowQueryExplainParser.Parse(explain, Now, rawPreviewBytes: 0);

        Assert.Equal(SlowQueryExplainStatus.Success, result.Status);
        Assert.Equal("COLLSCAN", result.WinningStage);
        Assert.True(result.HasCollScan);
        Assert.Empty(result.IndexNames);
    }

    [Fact]
    public void Parse_FindIxScan_CapturesIndexName_NoCollScan()
    {
        // FETCH wraps an IXSCAN — the canonical "index used" shape. Winning stage is FETCH at
        // the top level; the IXSCAN sits one level down via inputStage, so the recursive walk
        // must descend to find the index name.
        var explain = new BsonDocument
        {
            { "queryPlanner", new BsonDocument
                {
                    { "winningPlan", new BsonDocument
                        {
                            { "stage", "FETCH" },
                            { "inputStage", new BsonDocument
                                {
                                    { "stage", "IXSCAN" },
                                    { "indexName", "ckTypeId.fullName_1" }
                                }
                            }
                        }
                    }
                }
            }
        };

        var result = SlowQueryExplainParser.Parse(explain, Now, rawPreviewBytes: 0);

        Assert.Equal(SlowQueryExplainStatus.Success, result.Status);
        Assert.Equal("FETCH", result.WinningStage);
        Assert.False(result.HasCollScan);
        Assert.Equal(new[] { "ckTypeId.fullName_1" }, result.IndexNames);
    }

    [Fact]
    public void Parse_AggregateWithCursorStage_DescendsIntoCursorPlan()
    {
        // Aggregation explain output: stages[] with the first $cursor carrying the actual
        // queryPlanner. Downstream stages are in-memory transforms and don't touch the index.
        var explain = new BsonDocument
        {
            { "stages", new BsonArray
                {
                    new BsonDocument
                    {
                        { "$cursor", new BsonDocument
                            {
                                { "queryPlanner", new BsonDocument
                                    {
                                        { "winningPlan", new BsonDocument
                                            {
                                                { "stage", "IXSCAN" },
                                                { "indexName", "tenantId_1_timestamp_-1" }
                                            }
                                        }
                                    }
                                }
                            }
                        }
                    },
                    new BsonDocument { { "$match", new BsonDocument() } }
                }
            }
        };

        var result = SlowQueryExplainParser.Parse(explain, Now, rawPreviewBytes: 0);

        Assert.Equal(SlowQueryExplainStatus.Success, result.Status);
        Assert.Equal("IXSCAN", result.WinningStage);
        Assert.False(result.HasCollScan);
        Assert.Equal(new[] { "tenantId_1_timestamp_-1" }, result.IndexNames);
    }

    [Fact]
    public void Parse_PlanWithInputStagesArray_RecursesIntoAllChildren()
    {
        // SUBPLAN / OR shapes use inputStages (plural). Two child IXSCANs → both names captured.
        var explain = new BsonDocument
        {
            { "queryPlanner", new BsonDocument
                {
                    { "winningPlan", new BsonDocument
                        {
                            { "stage", "SUBPLAN" },
                            { "inputStages", new BsonArray
                                {
                                    new BsonDocument
                                    {
                                        { "stage", "IXSCAN" },
                                        { "indexName", "idx_a" }
                                    },
                                    new BsonDocument
                                    {
                                        { "stage", "IXSCAN" },
                                        { "indexName", "idx_b" }
                                    }
                                }
                            }
                        }
                    }
                }
            }
        };

        var result = SlowQueryExplainParser.Parse(explain, Now, rawPreviewBytes: 0);

        Assert.Equal(SlowQueryExplainStatus.Success, result.Status);
        Assert.Equal("SUBPLAN", result.WinningStage);
        Assert.False(result.HasCollScan);
        Assert.Equal(new[] { "idx_a", "idx_b" }, result.IndexNames);
    }

    [Fact]
    public void Parse_NestedCollScan_FlagsHasCollScan_EvenWhenTopStageIsFetch()
    {
        // FETCH wrapping a COLLSCAN — yes, this can happen with a $or where one branch has no
        // index. The top-level stage is FETCH, but the index situation is still broken — the
        // HasCollScan flag must surface that.
        var explain = new BsonDocument
        {
            { "queryPlanner", new BsonDocument
                {
                    { "winningPlan", new BsonDocument
                        {
                            { "stage", "FETCH" },
                            { "inputStage", new BsonDocument
                                {
                                    { "stage", "COLLSCAN" }
                                }
                            }
                        }
                    }
                }
            }
        };

        var result = SlowQueryExplainParser.Parse(explain, Now, rawPreviewBytes: 0);

        Assert.Equal(SlowQueryExplainStatus.Success, result.Status);
        Assert.Equal("FETCH", result.WinningStage);
        Assert.True(result.HasCollScan);
    }

    [Fact]
    public void Parse_WithPreviewBudget_StoresTruncatedJson()
    {
        var explain = new BsonDocument
        {
            { "queryPlanner", new BsonDocument
                {
                    { "winningPlan", new BsonDocument
                        {
                            { "stage", "IXSCAN" },
                            { "indexName", "x_1" }
                        }
                    },
                    { "padding", new string('x', 8000) }
                }
            }
        };

        var result = SlowQueryExplainParser.Parse(explain, Now, rawPreviewBytes: 256);

        Assert.NotNull(result.RawExplainPreview);
        Assert.True(System.Text.Encoding.UTF8.GetByteCount(result.RawExplainPreview!) <= 256 + 100);
        Assert.EndsWith("<truncated>", result.RawExplainPreview);
    }

    [Fact]
    public void Parse_WithZeroPreviewBudget_OmitsRawJson()
    {
        var explain = new BsonDocument
        {
            { "queryPlanner", new BsonDocument
                {
                    { "winningPlan", new BsonDocument { { "stage", "COLLSCAN" } } }
                }
            }
        };

        var result = SlowQueryExplainParser.Parse(explain, Now, rawPreviewBytes: 0);

        Assert.Null(result.RawExplainPreview);
    }

    [Theory]
    [InlineData("find", true)]
    [InlineData("aggregate", true)]
    [InlineData("count", true)]
    [InlineData("distinct", true)]
    [InlineData("findAndModify", true)]
    [InlineData("update", true)]
    [InlineData("delete", true)]
    [InlineData("mapReduce", true)]
    [InlineData("insert", false)]
    [InlineData("createIndexes", false)]
    [InlineData("listCollections", false)]
    [InlineData("ping", false)]
    public void IsExplainable_MatchesMongoDocs(string commandName, bool expected)
    {
        Assert.Equal(expected, SlowQueryExplainParser.IsExplainable(commandName));
    }

    [Fact]
    public void Unsupported_FactoryProducesUnsupportedStatusWithCommandName()
    {
        var result = SlowQueryExplainParser.Unsupported(Now, "insert");

        Assert.Equal(SlowQueryExplainStatus.Unsupported, result.Status);
        Assert.Contains("insert", result.ErrorMessage ?? string.Empty);
        Assert.Empty(result.IndexNames);
    }

    [Fact]
    public void Failure_FactoryProducesFailedStatusWithMessage()
    {
        var result = SlowQueryExplainParser.Failure(Now, "timeout");

        Assert.Equal(SlowQueryExplainStatus.Failed, result.Status);
        Assert.Equal("timeout", result.ErrorMessage);
    }
}
