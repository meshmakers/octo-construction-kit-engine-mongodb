using Meshmakers.Common.Shared;
using Meshmakers.Octo.Runtime.Contracts.MongoDb.Configuration;
using Meshmakers.Octo.Runtime.Contracts.MongoDb.Repositories;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using MongoDB.Bson;
using MongoDB.Driver;

namespace Meshmakers.Octo.Runtime.Engine.MongoDb.Repositories.MongoDb.Generic;

/// <summary>
/// Implementation of mongodb repository client for admin operations.
/// </summary>
public class AdminMongoRepositoryClient(
    ILogger<AdminMongoRepositoryClient> logger,
    IOptions<OctoSystemConfiguration> systemConfiguration,
    IServiceProvider serviceProvider, string databaseName)
    : MongoRepositoryClient(logger, systemConfiguration, serviceProvider), IAdminRepositoryClient
{
    protected override MongoUrl CreateConnectionUri()
    {
        var urlBuilder = new MongoUrlBuilder();

        var systemConfiguration = SystemConfiguration.Value;
        if (systemConfiguration.DatabaseHost.Contains(","))
        {
            urlBuilder.Servers =
                systemConfiguration.DatabaseHost.Split(",").Select(x => new MongoServerAddress(x));
        }
        else
        {
            urlBuilder.Server = new MongoServerAddress(systemConfiguration.DatabaseHost);
        }

        if (!string.IsNullOrWhiteSpace(systemConfiguration.DatabaseUser)
            && !string.IsNullOrWhiteSpace(systemConfiguration.DatabaseUserPassword))
        {
            urlBuilder.Username = systemConfiguration.AdminUser;
            urlBuilder.Password = systemConfiguration.AdminUserPassword;
            urlBuilder.DatabaseName = databaseName;
            urlBuilder.AuthenticationSource = systemConfiguration.AuthenticationDatabaseName;
        }

        urlBuilder.ApplicationName = $"OctoMesh-{databaseName}-{InstanceId}-{urlBuilder.Username}";
        urlBuilder.UseTls = systemConfiguration.UseTls;
        urlBuilder.AllowInsecureTls = systemConfiguration.AllowInsecureTls;
        urlBuilder.RetryReads = true;
        urlBuilder.RetryWrites = true;

        return urlBuilder.ToMongoUrl();
    }
    
    public async Task<IOctoAdminSession> GetAdminSessionAsync()
    {
        var session = await Client.StartSessionAsync();
        var logger = ServiceProvider.GetRequiredService<ILogger<OctoAdminSession>>();
        return new OctoAdminSession(logger, session, Client.Settings.ApplicationName);
    }

    public IOctoAdminSession GetSystemSession()
    {
        var session = Client.StartSession();
        var logger = ServiceProvider.GetRequiredService<ILogger<OctoAdminSession>>();
        return new OctoAdminSession(logger, session, Client.Settings.ApplicationName);
    }
    
    public Task CreateRepositoryAsync(string name)
    {
        ArgumentValidation.ValidateString(nameof(name), name);

        // MongoDB creates automatically databases. This method is
        // existing to keep that in mind for other dbms
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
    
    public async Task CreateUser(string authenticationDatabaseName, string databaseName,
        string user,
        string? password)
    {
        ArgumentValidation.ValidateString(nameof(authenticationDatabaseName), authenticationDatabaseName);
        ArgumentValidation.ValidateString(nameof(databaseName), databaseName);
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
                    new BsonDocument { { "role", "readWrite" }, { "db", databaseName } }
                }
            }
        };

        await database.RunCommandAsync(new BsonDocumentCommand<BsonDocument>(createUserCommand));
    }
}