using Meshmakers.Common.Shared;
using Meshmakers.Octo.Runtime.Contracts.MongoDb;
using Meshmakers.Octo.Runtime.Contracts.MongoDb.Configuration;
using Meshmakers.Octo.Runtime.Contracts.MongoDb.Repositories;

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using MongoDB.Bson;
using MongoDB.Driver;

namespace Meshmakers.Octo.Runtime.Engine.MongoDb.Repositories.MongoDb.Generic;

/// <summary>
///     Implementation of MongoDB repository client for admin operations.
/// </summary>
public class AdminMongoRepositoryClient(
    ILogger<AdminMongoRepositoryClient> logger,
    IOptions<OctoSystemConfiguration> systemConfiguration,
    IServiceProvider serviceProvider,
    string databaseName)
    : MongoRepositoryClient(logger, systemConfiguration, serviceProvider), IAdminRepositoryClient
{
    public async Task<IOctoAdminSession> GetAdminSessionAsync()
    {
        var session = await Client.StartSessionAsync();
        var logger = _serviceProvider.GetRequiredService<ILogger<OctoAdminSession>>();
        return new OctoAdminSession(logger, session, Client.Settings.ApplicationName);
    }

    public IOctoAdminSession GetSystemSession()
    {
        var session = Client.StartSession();
        var logger = _serviceProvider.GetRequiredService<ILogger<OctoAdminSession>>();
        return new OctoAdminSession(logger, session, Client.Settings.ApplicationName);
    }

    public Task CreateRepositoryAsync(string name)
    {
        ArgumentValidation.ValidateString(nameof(name), name);

        // MongoDB automatically creates databases. This method is
        // existing to keep that in mind for other DBMS
        return Task.CompletedTask;
    }

    public async Task DropRepositoryAsync(string name)
    {
        ArgumentValidation.ValidateString(nameof(name), name);

        await Client.DropDatabaseAsync(name);
    }

    public async Task<bool> IsRepositoryExistingAsync(string name)
    {
        var databaseNames = await Client.ListDatabaseNamesAsync();

        return databaseNames.ToList().Any(x => string.Compare(x, name,
            StringComparison.InvariantCultureIgnoreCase) == 0);
    }

    public async Task<IReadOnlyList<string>> ListCollectionNamesAsync(string databaseName)
    {
        ArgumentValidation.ValidateString(nameof(databaseName), databaseName);

        // GetDatabase(name) is case-sensitive while IsRepositoryExistingAsync matches ignore-case, so
        // resolve the actually stored database name(s) the same ignore-case way — otherwise a
        // differently-cased existing database would report no collections and misclassify as an
        // empty, bootstrappable shell (AB#4854).
        var databaseNames = await Client.ListDatabaseNamesAsync();
        var matchingNames = databaseNames.ToList().Where(x => string.Compare(x, databaseName,
            StringComparison.InvariantCultureIgnoreCase) == 0);

        var collectionNames = new List<string>();
        foreach (var matchingName in matchingNames)
        {
            var cursor = await Client.GetDatabase(matchingName).ListCollectionNamesAsync();
            collectionNames.AddRange(await cursor.ToListAsync());
        }

        return collectionNames;
    }

    public async Task CreateUser(string authenticationDatabaseName, string userDatabaseName,
        string user,
        string? password)
    {
        ArgumentValidation.ValidateString(nameof(authenticationDatabaseName), authenticationDatabaseName);
        ArgumentValidation.ValidateString(nameof(userDatabaseName), userDatabaseName);
        ArgumentValidation.ValidateString(nameof(user), user);
        ArgumentValidation.ValidateString(nameof(password), password);

        var database = Client.GetDatabase(authenticationDatabaseName);

        var result = await database.RunCommandAsync<BsonDocument>("{usersInfo: '" + user + "'}");
        if (result.GetValue("ok").AsDouble > 0 && result.GetValue("users").AsBsonArray.Count > 0)
        {
            return;
        }

        var createUserCommand = new BsonDocument
        {
            { "createUser", user },
            { "pwd", password },
            {
                "roles", new BsonArray
                {
                    new BsonDocument { { "role", "readWrite" }, { "db", userDatabaseName } }
                }
            }
        };

        try
        {
            await database.RunCommandAsync(new BsonDocumentCommand<BsonDocument>(createUserCommand));
        }
        catch (MongoCommandException e) when (e.Code == 51003)
        {
            // 51003 = user already exists: a second replica raced us between the usersInfo check and
            // the createUser command. The user is there, which is all this method guarantees.
        }
    }

    public async Task DropUser(string authenticationDatabaseName, string user)
    {
        ArgumentValidation.ValidateString(nameof(authenticationDatabaseName), authenticationDatabaseName);
        ArgumentValidation.ValidateString(nameof(user), user);

        var database = Client.GetDatabase(authenticationDatabaseName);

        var result = await database.RunCommandAsync<BsonDocument>("{usersInfo: '" + user + "'}");
        if (result.GetValue("ok").AsDouble <= 0 || result.GetValue("users").AsBsonArray.Count == 0)
        {
            // User does not exist (e.g. its creation is what failed) - nothing to roll back.
            return;
        }

        var dropUserCommand = new BsonDocument { { "dropUser", user } };
        await database.RunCommandAsync(new BsonDocumentCommand<BsonDocument>(dropUserCommand));
    }

    protected override MongoUrl CreateConnectionUri()
    {
        var urlBuilder = new MongoUrlBuilder();

        var systemConfiguration = _systemConfiguration.Value;
        // Parse, not the string ctor: DatabaseHost may be "host:port", which MongoDB.Driver >= 3.11.1 rejects in the ctor (CSHARP-6171).
        if (systemConfiguration.DatabaseHost.Contains(","))
        {
            urlBuilder.Servers =
                systemConfiguration.DatabaseHost.Split(",").Select(MongoServerAddress.Parse);
        }
        else
        {
            urlBuilder.Server = MongoServerAddress.Parse(systemConfiguration.DatabaseHost);
        }

        if (!string.IsNullOrWhiteSpace(systemConfiguration.AdminUser)
            && !string.IsNullOrWhiteSpace(systemConfiguration.AdminUserPassword))
        {
            urlBuilder.Username = systemConfiguration.AdminUser;
            urlBuilder.Password = systemConfiguration.AdminUserPassword;
            urlBuilder.DatabaseName = databaseName;
            urlBuilder.AuthenticationSource = systemConfiguration.AuthenticationDatabaseName;
        }
        else
        {
            throw TenantException.AdminCredentialsMissing();
        }

        urlBuilder.ApplicationName = BuildApplicationName(databaseName, _instanceId, urlBuilder.Username);
        urlBuilder.UseTls = systemConfiguration.UseTls;
        urlBuilder.AllowInsecureTls = systemConfiguration.AllowInsecureTls;
        urlBuilder.RetryReads = true;
        urlBuilder.RetryWrites = true;
        urlBuilder.DirectConnection = systemConfiguration.UseDirectConnection;

        if (!string.IsNullOrWhiteSpace(systemConfiguration.ReplicaSetName))
        {
            urlBuilder.ReplicaSetName = systemConfiguration.ReplicaSetName;
        }

        return urlBuilder.ToMongoUrl();
    }
}
