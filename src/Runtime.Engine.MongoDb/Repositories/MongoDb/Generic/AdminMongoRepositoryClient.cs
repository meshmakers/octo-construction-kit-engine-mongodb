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

        await database.RunCommandAsync(new BsonDocumentCommand<BsonDocument>(createUserCommand));
    }

    protected override MongoUrl CreateConnectionUri()
    {
        var urlBuilder = new MongoUrlBuilder();

        var systemConfiguration = _systemConfiguration.Value;
        if (systemConfiguration.DatabaseHost.Contains(","))
        {
            urlBuilder.Servers =
                systemConfiguration.DatabaseHost.Split(",").Select(x => new MongoServerAddress(x));
        }
        else
        {
            urlBuilder.Server = new MongoServerAddress(systemConfiguration.DatabaseHost);
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

        urlBuilder.ApplicationName = $"OctoMesh-{databaseName}-{_instanceId}-{urlBuilder.Username}";
        urlBuilder.UseTls = systemConfiguration.UseTls;
        urlBuilder.AllowInsecureTls = systemConfiguration.AllowInsecureTls;
        urlBuilder.RetryReads = true;
        urlBuilder.RetryWrites = true;
        urlBuilder.DirectConnection = systemConfiguration.UseDirectConnection;
        
        return urlBuilder.ToMongoUrl();
    }
}
