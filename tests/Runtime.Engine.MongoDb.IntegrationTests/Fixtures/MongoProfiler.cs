using Meshmakers.Octo.Runtime.Contracts.MongoDb.Configuration;

using MongoDB.Bson;
using MongoDB.Driver;

namespace Meshmakers.Octo.Runtime.Engine.MongoDb.IntegrationTests.Fixtures;

/// <summary>
///     Records what MongoDB received while a test step runs (database profiler, level 2) - for assertions on command
///     options such as <c>maxTimeMS</c> that no result shows. Profiling needs the admin user, the tenant user may not
///     run <c>profile</c>. Use it only in collections whose tests run serially on their own database.
/// </summary>
internal static class MongoProfiler
{
    /// <summary>
    ///     Runs <paramref name="run" /> with the profiler of <paramref name="databaseName" /> on and returns the profiled
    ///     aggregate commands (counts included: <c>countDocuments</c> is an aggregate on the server).
    /// </summary>
    public static Task<List<BsonDocument>> ProfileAggregatesAsync(OctoSystemConfiguration config,
        string databaseName, Func<Task> run) =>
        ProfileAsync(config, databaseName, run, new BsonDocument("command.aggregate", new BsonDocument("$exists", true)));

    /// <summary>
    ///     Runs <paramref name="run" /> with the profiler of <paramref name="databaseName" /> on and returns the profiled
    ///     reads: <c>find</c> and <c>aggregate</c> commands.
    /// </summary>
    public static Task<List<BsonDocument>> ProfileReadsAsync(OctoSystemConfiguration config,
        string databaseName, Func<Task> run) =>
        ProfileAsync(config, databaseName, run, new BsonDocument("$or", new BsonArray
        {
            new BsonDocument("command.find", new BsonDocument("$exists", true)),
            new BsonDocument("command.aggregate", new BsonDocument("$exists", true))
        }));

    private static async Task<List<BsonDocument>> ProfileAsync(OctoSystemConfiguration config,
        string databaseName, Func<Task> run, BsonDocument filter)
    {
        var urlBuilder = new MongoUrlBuilder
        {
            Server = MongoServerAddress.Parse(config.DatabaseHost),
            Username = config.AdminUser,
            Password = config.AdminUserPassword,
            AuthenticationSource = config.AuthenticationDatabaseName,
            DatabaseName = config.AuthenticationDatabaseName,
            DirectConnection = config.UseDirectConnection
        };
        using var client = new MongoClient(urlBuilder.ToMongoUrl());
        var database = client.GetDatabase(databaseName);

        await database.RunCommandAsync<BsonDocument>(new BsonDocument("profile", 0));
        await database.DropCollectionAsync("system.profile");
        await database.RunCommandAsync<BsonDocument>(new BsonDocument("profile", 2));
        try
        {
            await run();
        }
        finally
        {
            await database.RunCommandAsync<BsonDocument>(new BsonDocument("profile", 0));
        }

        return await database.GetCollection<BsonDocument>("system.profile")
            .Find(filter).ToListAsync();
    }
}
