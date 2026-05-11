using MongoDB.Bson;
using MongoDB.Bson.Serialization;
using MongoDB.Bson.Serialization.Serializers;
using MongoDB.Driver;

using Meshmakers.Octo.Runtime.Engine.MongoDb.Repositories.MongoDb.Generic;

namespace Meshmakers.Octo.Runtime.Engine.CrateDb.UnitTests;

public class WatchFilterCompositionTests
{
    // Use BsonDocument as the inner document type so the ChangeStreamDocumentSerializer
    // can be instantiated without a parameterless constructor requirement.
    // All filters use raw string field paths so the inner serializer is only used
    // for field name resolution, not data deserialization.
    private static BsonDocument Render(FilterDefinition<ChangeStreamDocument<BsonDocument>> filter)
    {
        var registry = BsonSerializer.SerializerRegistry;
        var serializer = new ChangeStreamDocumentSerializer<BsonDocument>(BsonDocumentSerializer.Instance);
        var renderArgs = new RenderArgs<ChangeStreamDocument<BsonDocument>>(serializer, registry);
        return filter.Render(renderArgs);
    }

    private static FilterDefinition<ChangeStreamDocument<BsonDocument>> MakeAfter(string name)
        => Builders<ChangeStreamDocument<BsonDocument>>.Filter.Eq("fullDocument.Name", name);

    private static FilterDefinition<ChangeStreamDocument<BsonDocument>> MakeBefore(string name)
        => Builders<ChangeStreamDocument<BsonDocument>>.Filter.Eq("fullDocumentBeforeChange.Name", name);

    [Fact]
    public void Compose_returns_null_when_both_filters_null()
    {
        var result = BuildExtensions.ComposeWatchFilter<BsonDocument>(null, null);

        Assert.Null(result);
    }

    [Fact]
    public void Compose_returns_after_filter_when_only_after_is_set()
    {
        var after = MakeAfter("alice");

        var result = BuildExtensions.ComposeWatchFilter(after, null);

        Assert.NotNull(result);
        var rendered = Render(result!);
        Assert.True(rendered.Contains("fullDocument.Name"));
        Assert.False(rendered.Contains("$and"));
    }

    [Fact]
    public void Compose_returns_before_filter_when_only_before_is_set()
    {
        var before = MakeBefore("alice");

        var result = BuildExtensions.ComposeWatchFilter<BsonDocument>(null, before);

        Assert.NotNull(result);
        var rendered = Render(result!);
        Assert.True(rendered.Contains("fullDocumentBeforeChange.Name"));
        Assert.False(rendered.Contains("$and"));
    }

    [Fact]
    public void Compose_uses_And_not_Or_when_both_filters_set()
    {
        // Regression for Bug 2: the previous code used Filter.Or which let events
        // through when only one image matched.
        //
        // MongoDB Driver 3.x flattens And(Eq(fieldA,...), Eq(fieldB,...)) on distinct
        // field paths into a merged document { fieldA: ..., fieldB: ... } without an
        // explicit $and wrapper.  Whether the driver emits $and or a flat doc, the
        // contract is: both filter fields must be present AND $or must never appear.
        var after = MakeAfter("alice");
        var before = MakeBefore("bob");

        var result = BuildExtensions.ComposeWatchFilter(after, before);

        Assert.NotNull(result);
        var rendered = Render(result!);
        // Both field paths must survive regardless of $and vs flat-merge form.
        Assert.True(rendered.Contains("fullDocument.Name") || ContainsInAnd(rendered, "fullDocument.Name"),
            $"Expected fullDocument.Name to be present, got: {rendered}");
        Assert.True(rendered.Contains("fullDocumentBeforeChange.Name") || ContainsInAnd(rendered, "fullDocumentBeforeChange.Name"),
            $"Expected fullDocumentBeforeChange.Name to be present, got: {rendered}");
        // Must never use $or — that is the core Bug 2 regression guard.
        Assert.False(rendered.Contains("$or"), $"Must not use $or: {rendered}");
    }

    [Fact]
    public void Compose_And_preserves_both_branches()
    {
        var after = MakeAfter("alice");
        var before = MakeBefore("bob");

        var result = BuildExtensions.ComposeWatchFilter(after, before);
        var rendered = Render(result!);

        // MongoDB Driver 3.x may flatten And on distinct fields into a flat doc.
        // Accept either: $and array with 2 elements, OR flat doc with both fields.
        var renderedString = rendered.ToString();
        Assert.Contains("fullDocument.Name", renderedString);
        Assert.Contains("fullDocumentBeforeChange.Name", renderedString);
        Assert.DoesNotContain("$or", renderedString);
    }

    // Helper: checks if a field path appears somewhere inside the $and array.
    private static bool ContainsInAnd(BsonDocument doc, string fieldPath)
    {
        if (!doc.Contains("$and")) return false;
        var andArray = doc["$and"].AsBsonArray;
        return andArray.Any(item => item.IsBsonDocument && item.AsBsonDocument.Contains(fieldPath));
    }
}
