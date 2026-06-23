using Meshmakers.Octo.ConstructionKit.Contracts.Services;

using MongoDB.Bson;

namespace Meshmakers.Octo.Runtime.Engine.MongoDb.Repositories.MongoDb.Generic;

/// <summary>
/// Parses MongoDB's <c>explain</c> output (verbosity = <c>queryPlanner</c>) into a small
/// <see cref="SlowQueryExplain"/> POCO carrying the signals Stage 2B's Studio surface needs:
/// the top-level winning stage, a COLLSCAN flag, and the IXSCAN index names.
/// </summary>
/// <remarks>
/// <para>
/// The driver returns different shapes depending on the command:
/// </para>
/// <list type="bullet">
///   <item>
///     <c>find</c>, <c>count</c>, <c>distinct</c>, <c>findAndModify</c>, <c>update</c>,
///     <c>delete</c> → <c>queryPlanner.winningPlan</c> at the top level.
///   </item>
///   <item>
///     <c>aggregate</c> → <c>stages</c> array; the first stage is conventionally
///     <c>$cursor</c> carrying its own <c>queryPlanner.winningPlan</c>. Downstream stages are
///     in-memory transforms — we only walk the cursor's plan because the others don't touch
///     the collection.
///   </item>
/// </list>
/// <para>
/// Recursive walk descends through <c>inputStage</c> (single child) and <c>inputStages</c>
/// (array of children) so a <c>FETCH → IXSCAN</c> wrapper is reported as <c>WinningStage=FETCH</c>
/// with the inner index name captured in <see cref="SlowQueryExplain.IndexNames"/>.
/// </para>
/// </remarks>
public static class SlowQueryExplainParser
{
    /// <summary>
    /// Parses <paramref name="explainResult"/> into the lightweight diagnostic view. Always
    /// returns a non-null instance; on malformed input the result has
    /// <see cref="SlowQueryExplainStatus.Failed"/> with the cause in
    /// <see cref="SlowQueryExplain.ErrorMessage"/>. <paramref name="rawPreviewBytes"/> caps the
    /// truncated queryPlanner JSON stored on the result (0 = no preview).
    /// </summary>
    /// <remarks>
    /// When <paramref name="originalCommand"/>, <paramref name="commandName"/>, and
    /// <paramref name="target"/> are all supplied AND the parsed plan flags
    /// <c>HasCollScan = true</c>, <see cref="SlowQueryIndexSuggester.TrySuggest"/> is invoked
    /// and the result attached to <see cref="SlowQueryExplain.IndexSuggestion"/>. When the
    /// triple is omitted (legacy callers, unit tests of pure parsing), the suggestion is
    /// always <c>null</c>. <paramref name="tenantId"/> + <paramref name="ckCacheService"/>
    /// (Stage 2D) are forwarded to the suggester for opportunistic CK-YAML emission; both
    /// null produces a pure MongoDB suggestion (Stage 2C shape).
    /// </remarks>
    public static SlowQueryExplain Parse(
        BsonDocument? explainResult,
        DateTimeOffset capturedAt,
        int rawPreviewBytes,
        BsonDocument? originalCommand = null,
        string? commandName = null,
        string? target = null,
        string? tenantId = null,
        ICkCacheService? ckCacheService = null)
    {
        if (explainResult is null)
        {
            return Failure(capturedAt, "null explain result");
        }

        try
        {
            var planner = ExtractQueryPlanner(explainResult);
            if (planner is null)
            {
                return Failure(capturedAt, "no queryPlanner in explain result");
            }

            if (!planner.TryGetValue("winningPlan", out var winningPlanVal) ||
                winningPlanVal is not BsonDocument winningPlan)
            {
                return Failure(capturedAt, "no winningPlan in queryPlanner");
            }

            var winningStage = winningPlan.TryGetValue("stage", out var stageVal) && stageVal.IsString
                ? stageVal.AsString
                : "(unknown)";

            var indexNames = new List<string>();
            var hasCollScan = false;
            WalkPlan(winningPlan, indexNames, ref hasCollScan);

            // Preview the queryPlanner sub-document (not the full explain) — that's the
            // structurally interesting part for diagnosis. The full result also carries
            // serverInfo, command echo, etc., which would consume the preview budget without
            // adding signal.
            var preview = rawPreviewBytes > 0
                ? MongoCommandObservability.TruncateBson(planner, rawPreviewBytes)
                : null;

            // Stage 2C — when the plan reports a COLLSCAN and the dispatcher passed through
            // the original command, ask the suggester for an actionable index. Suggestion is
            // attached even for an UNK winning stage as long as HasCollScan flipped — the
            // missing-index symptom is what matters, not the surrounding stage shape.
            SlowQueryIndexSuggestion? suggestion = null;
            if (hasCollScan && originalCommand is not null &&
                !string.IsNullOrEmpty(commandName) && !string.IsNullOrEmpty(target))
            {
                suggestion = SlowQueryIndexSuggester.TrySuggest(
                    originalCommand, commandName, target,
                    tenantId: tenantId, ckCacheService: ckCacheService);
            }

            return new SlowQueryExplain(
                CapturedAt: capturedAt,
                Status: SlowQueryExplainStatus.Success,
                WinningStage: winningStage,
                HasCollScan: hasCollScan,
                IndexNames: indexNames,
                RawExplainPreview: preview,
                ErrorMessage: null,
                IndexSuggestion: suggestion);
        }
        catch (Exception ex)
        {
            return Failure(capturedAt, ex.GetType().Name + ": " + ex.Message);
        }
    }

    /// <summary>
    /// Convenience factory for the <c>Unsupported</c> outcome, used when the command type
    /// itself can't be passed to <c>explain</c>.
    /// </summary>
    public static SlowQueryExplain Unsupported(DateTimeOffset capturedAt, string commandName)
        => new(capturedAt, SlowQueryExplainStatus.Unsupported,
            WinningStage: string.Empty,
            HasCollScan: false,
            IndexNames: Array.Empty<string>(),
            RawExplainPreview: null,
            ErrorMessage: $"command type '{commandName}' is not explainable");

    /// <summary>
    /// Convenience factory for the <c>Failed</c> outcome with a custom message — used by the
    /// dispatcher when the driver call itself throws or times out.
    /// </summary>
    public static SlowQueryExplain Failure(DateTimeOffset capturedAt, string errorMessage)
        => new(capturedAt, SlowQueryExplainStatus.Failed,
            WinningStage: string.Empty,
            HasCollScan: false,
            IndexNames: Array.Empty<string>(),
            RawExplainPreview: null,
            ErrorMessage: errorMessage);

    /// <summary>
    /// Returns <c>true</c> when the driver-level command name supports the
    /// <c>explain</c> command at <c>queryPlanner</c> verbosity. The MongoDB docs are explicit:
    /// queryPlanner-verbosity explain on a write command (<c>update</c>, <c>delete</c>,
    /// <c>findAndModify</c>) does NOT execute the write — it's safe to schedule.
    /// </summary>
    public static bool IsExplainable(string commandName) => commandName switch
    {
        "find" => true,
        "aggregate" => true,
        "count" => true,
        "distinct" => true,
        "findAndModify" => true,
        "update" => true,
        "delete" => true,
        "mapReduce" => true,
        _ => false
    };

    /// <summary>
    /// Locates the <c>queryPlanner</c> sub-document. For <c>find</c>-style results it's at the
    /// top level; for <c>aggregate</c> results we descend into the first <c>$cursor</c> stage
    /// (the only one that actually queries the collection — downstream pipeline stages are
    /// in-memory transforms with no plan worth capturing).
    /// </summary>
    private static BsonDocument? ExtractQueryPlanner(BsonDocument explainResult)
    {
        if (explainResult.TryGetValue("queryPlanner", out var directPlanner) &&
            directPlanner is BsonDocument planner)
        {
            return planner;
        }

        if (explainResult.TryGetValue("stages", out var stagesVal) && stagesVal is BsonArray stages)
        {
            foreach (var stage in stages)
            {
                if (stage is not BsonDocument stageDoc)
                {
                    continue;
                }

                if (stageDoc.TryGetValue("$cursor", out var cursorVal) &&
                    cursorVal is BsonDocument cursorDoc &&
                    cursorDoc.TryGetValue("queryPlanner", out var nested) &&
                    nested is BsonDocument nestedPlanner)
                {
                    return nestedPlanner;
                }
            }
        }

        return null;
    }

    /// <summary>
    /// Depth-first walk of a winning-plan tree. Collects every <c>IXSCAN.indexName</c> in
    /// document order and flips <paramref name="hasCollScan"/> on first <c>COLLSCAN</c>.
    /// Recurses through <c>inputStage</c> (single child, the common shape) and
    /// <c>inputStages</c> (array of children, used by <c>OR</c>/<c>SUBPLAN</c>).
    /// </summary>
    private static void WalkPlan(BsonDocument plan, List<string> indexNames, ref bool hasCollScan)
    {
        if (plan.TryGetValue("stage", out var stageVal) && stageVal.IsString)
        {
            switch (stageVal.AsString)
            {
                case "COLLSCAN":
                    hasCollScan = true;
                    break;
                case "IXSCAN":
                    if (plan.TryGetValue("indexName", out var nameVal) && nameVal.IsString)
                    {
                        indexNames.Add(nameVal.AsString);
                    }
                    break;
            }
        }

        if (plan.TryGetValue("inputStage", out var inputStageVal) &&
            inputStageVal is BsonDocument inputStage)
        {
            WalkPlan(inputStage, indexNames, ref hasCollScan);
        }

        if (plan.TryGetValue("inputStages", out var inputStagesVal) &&
            inputStagesVal is BsonArray inputStages)
        {
            foreach (var child in inputStages)
            {
                if (child is BsonDocument childDoc)
                {
                    WalkPlan(childDoc, indexNames, ref hasCollScan);
                }
            }
        }
    }
}
