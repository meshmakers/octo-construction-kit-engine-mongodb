using MongoDB.Bson;
using MongoDB.Bson.Serialization;
using MongoDB.Driver;

using Meshmakers.Octo.Runtime.Engine.MongoDb.Repositories.MongoDb.Generic;

using MongoDbConstants = Meshmakers.Octo.Runtime.Engine.MongoDb.Constants;

namespace Meshmakers.Octo.Runtime.Engine.MongoDb.StreamData.UnitTests;

public class BuildExtensionsInjectTests
{
    private sealed record Inner(string Name, int Age);
    private sealed record Outer(Inner Inner);

    private static BsonDocument Render<T>(FilterDefinition<T> filter)
    {
        var registry = BsonSerializer.SerializerRegistry;
        var renderArgs = new RenderArgs<T>(registry.GetSerializer<T>(), registry);
        return filter.Render(renderArgs);
    }

    [Fact]
    public void Inject_prefixes_simple_equality_filter_with_fullDocument()
    {
        var innerFilter = Builders<Inner>.Filter.Eq(i => i.Name, "alice");

        var injected = Builders<Outer>.Filter.Inject(
            MongoDbConstants.ChangeStreamFullDocument, innerFilter);

        var rendered = Render(injected);
        Assert.True(rendered.Contains("fullDocument.Name"));
        Assert.Equal("alice", rendered["fullDocument.Name"].AsString);
    }

    [Fact]
    public void Inject_prefixes_simple_equality_filter_with_fullDocumentBeforeChange()
    {
        // Regression for Bug 1: before this fix, Inject ignored its fieldName
        // argument and hardcoded "fullDocument.", so a filter intended for the
        // pre-image was silently evaluated against the post-image.
        var innerFilter = Builders<Inner>.Filter.Eq(i => i.Name, "alice");

        var injected = Builders<Outer>.Filter.Inject(
            MongoDbConstants.ChangeStreamFullDocumentBeforeChange, innerFilter);

        var rendered = Render(injected);
        Assert.True(rendered.Contains("fullDocumentBeforeChange.Name"),
            $"Expected pre-image prefix, got: {rendered}");
        Assert.False(rendered.Contains("fullDocument.Name"),
            "Must not evaluate pre-image filter against post-image field");
    }

    [Fact]
    public void Inject_prefixes_all_fields_in_And_filter()
    {
        // MongoDB Driver 3.x optimizes And(Eq, Gt) on distinct fields to a
        // flat document { "Name": ..., "Age": { "$gt": ... } } rather than
        // { "$and": [ ... ] }. The test verifies both fields are prefixed.
        var innerFilter = Builders<Inner>.Filter.And(
            Builders<Inner>.Filter.Eq(i => i.Name, "alice"),
            Builders<Inner>.Filter.Gt(i => i.Age, 30));

        var injected = Builders<Outer>.Filter.Inject(
            MongoDbConstants.ChangeStreamFullDocument, innerFilter);

        var rendered = Render(injected);
        Assert.True(rendered.Contains("fullDocument.Name"),
            $"Expected fullDocument.Name, got: {rendered}");
        Assert.True(rendered.Contains("fullDocument.Age"),
            $"Expected fullDocument.Age, got: {rendered}");
    }

    [Fact]
    public void Inject_prefixes_all_fields_in_Or_filter()
    {
        var innerFilter = Builders<Inner>.Filter.Or(
            Builders<Inner>.Filter.Eq(i => i.Name, "alice"),
            Builders<Inner>.Filter.Eq(i => i.Name, "bob"));

        var injected = Builders<Outer>.Filter.Inject(
            MongoDbConstants.ChangeStreamFullDocument, innerFilter);

        var rendered = Render(injected);
        var orArray = rendered["$or"].AsBsonArray;
        Assert.Equal(2, orArray.Count);
        Assert.True(orArray[0].AsBsonDocument.Contains("fullDocument.Name"));
        Assert.True(orArray[1].AsBsonDocument.Contains("fullDocument.Name"));
    }

    [Fact]
    public void Inject_prefixes_fields_in_nested_operators()
    {
        // And(Or(...), Gte) — the $or is on the same field so it stays as $or;
        // And with an $or branch and a separate Gte field stays flat (no $and wrapper).
        var innerFilter = Builders<Inner>.Filter.And(
            Builders<Inner>.Filter.Or(
                Builders<Inner>.Filter.Eq(i => i.Name, "alice"),
                Builders<Inner>.Filter.Eq(i => i.Name, "bob")),
            Builders<Inner>.Filter.Gte(i => i.Age, 18));

        var injected = Builders<Outer>.Filter.Inject(
            MongoDbConstants.ChangeStreamFullDocumentBeforeChange, innerFilter);

        var rendered = Render(injected);
        // The $or branch on Name is nested inside the rendered document
        Assert.True(rendered.Contains("$or"),
            $"Expected $or in rendered document, got: {rendered}");
        var orBranch = rendered["$or"].AsBsonArray;
        Assert.True(orBranch[0].AsBsonDocument.Contains("fullDocumentBeforeChange.Name"),
            $"Expected fullDocumentBeforeChange.Name in $or branch, got: {orBranch}");
        Assert.True(rendered.Contains("fullDocumentBeforeChange.Age"),
            $"Expected fullDocumentBeforeChange.Age, got: {rendered}");
    }

    [Fact]
    public void Inject_handles_In_operator_with_array_value()
    {
        var innerFilter = Builders<Inner>.Filter.In(i => i.Name, new[] { "alice", "bob" });

        var injected = Builders<Outer>.Filter.Inject(
            MongoDbConstants.ChangeStreamFullDocument, innerFilter);

        var rendered = Render(injected);
        Assert.True(rendered.Contains("fullDocument.Name"));
        Assert.Equal(BsonType.Array, rendered["fullDocument.Name"].AsBsonDocument["$in"].BsonType);
    }

    [Fact]
    public void Inject_handles_empty_filter()
    {
        var innerFilter = Builders<Inner>.Filter.Empty;

        var injected = Builders<Outer>.Filter.Inject(
            MongoDbConstants.ChangeStreamFullDocument, innerFilter);

        var rendered = Render(injected);
        Assert.Empty(rendered);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Inject_throws_when_fieldName_is_null_empty_or_whitespace(string? fieldName)
    {
        var innerFilter = Builders<Inner>.Filter.Eq(i => i.Name, "alice");

        Assert.ThrowsAny<ArgumentException>(
            () => Builders<Outer>.Filter.Inject(fieldName!, innerFilter));
    }
}
