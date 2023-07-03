using System;
using System.Linq;
using System.Threading.Tasks;
using Meshmakers.Common.Shared;
using Meshmakers.Octo.SystematizedData.Persistence.DataAccess;
using Meshmakers.Octo.SystematizedData.Persistence.DatabaseEntities;
using MongoDB.Bson;
using MongoDB.Bson.Serialization;
using MongoDB.Bson.Serialization.Conventions;
using MongoDB.Bson.Serialization.IdGenerators;
using MongoDB.Driver;

namespace Meshmakers.Octo.SystematizedData.Persistence.MongoDb;

public class MongoRepositoryClient : IRepositoryClient
{
    private readonly MongoClient _client;
    private const string OctoConventionCamelCase ="octo-convention-camelCase";
    private const string OctoConventionSerialization ="octo-convention-serialization";

    public MongoRepositoryClient(MongoConnectionOptions mongoConnectionOptions)
    {
        ArgumentValidation.ValidateString(nameof(mongoConnectionOptions.MongoDbHost),
            mongoConnectionOptions.MongoDbHost);


        var urlBuilder = new MongoUrlBuilder();

        if (mongoConnectionOptions.MongoDbHost.Contains(","))
        {
            urlBuilder.Servers =
                mongoConnectionOptions.MongoDbHost.Split(",").Select(x => new MongoServerAddress(x));
        }
        else
        {
            urlBuilder.Server = new MongoServerAddress(mongoConnectionOptions.MongoDbHost);
        }

        if (!string.IsNullOrWhiteSpace(mongoConnectionOptions.MongoDbUsername)
            && !string.IsNullOrWhiteSpace(mongoConnectionOptions.MongoDbPassword))
        {
            urlBuilder.Username = mongoConnectionOptions.MongoDbUsername;
            urlBuilder.Password = mongoConnectionOptions.MongoDbPassword;
            urlBuilder.DatabaseName = mongoConnectionOptions.DatabaseName;
            urlBuilder.AuthenticationSource = mongoConnectionOptions.AuthenticationSource;
        }

        urlBuilder.UseTls = mongoConnectionOptions.UseTls;
        urlBuilder.AllowInsecureTls = mongoConnectionOptions.AllowInsecureTls;
        // TODO: It seams that secondary servers do not have any work. This seems not be possibly. Other solution?
        // urlBuilder.ReadPreference = ReadPreference.SecondaryPreferred; 

        ConfigureMongoDriver();
        _client = new MongoClient(urlBuilder.ToMongoUrl());

        RegisterClassMaps();
    }

    private static void RegisterClassMaps()
    {
        BsonClassMap.TryRegisterClassMap<CkEntity>(cm =>
        {
            cm.SetIgnoreExtraElements(true);
            cm.MapIdMember(c => c.CkId).SetIsRequired(true).SetIdGenerator(new NullIdChecker());
            cm.MapMember(c => c.ScopeId).SetIsRequired(true);
            cm.MapMember(c => c.IsFinal).SetIsRequired(true);
            cm.MapMember(c => c.IsAbstract).SetIsRequired(true);
            cm.MapMember(c => c.Attributes).SetIsRequired(true);
            cm.MapMember(c => c.Indexes).SetIgnoreIfDefault(true);
            cm.MapMember(c => c.EnableChangeStreamPreAndPostImages).SetIgnoreIfDefault(true);
        });
        
        BsonClassMap.TryRegisterClassMap<CkAttribute>(cm =>
        {
            cm.SetIgnoreExtraElements(true);
            cm.MapIdMember(c => c.AttributeId).SetIsRequired(true).SetIdGenerator(new NullIdChecker());
            cm.MapMember(c => c.ScopeId).SetIsRequired(true);
            cm.MapMember(c => c.AttributeValueType).SetIsRequired(true);
            cm.MapMember(c => c.DefaultValue).SetIgnoreIfDefault(true);
            cm.MapMember(c => c.DefaultValues).SetIsRequired(true);
            cm.MapMember(c => c.SelectionValues).SetIgnoreIfDefault(true);
        });
        
        BsonClassMap.TryRegisterClassMap<CkEntityAssociation>(cm =>
        {
            cm.SetIgnoreExtraElements(true);
            cm.MapIdMember(c => c.AssociationId).SetSerializer(new OctoObjectIdSerializer()).SetIdGenerator(new OctoObjectIdGenerator());
            cm.MapMember(c => c.ScopeId).SetIsRequired(true);
            cm.MapMember(c => c.OriginCkId).SetIsRequired(true);
            cm.MapMember(c => c.TargetCkId).SetIsRequired(true);
            cm.MapMember(c => c.InboundName).SetIsRequired(true);
            cm.MapMember(c => c.OutboundName).SetIsRequired(true);
            cm.MapMember(c => c.InboundMultiplicity).SetIsRequired(true);
            cm.MapMember(c => c.OutboundMultiplicity).SetIsRequired(true);
            cm.MapMember(c => c.RoleId).SetIsRequired(true);
        });
        
        BsonClassMap.TryRegisterClassMap<CkEntityAttribute>(cm =>
        {
            cm.SetIgnoreExtraElements(true);
            cm.MapIdMember(c => c.AttributeId).SetIsRequired(true).SetIdGenerator(new NullIdChecker());
            cm.MapMember(c => c.AttributeName).SetIsRequired(true);
            cm.MapMember(c => c.IsAutoCompleteEnabled);
            cm.MapMember(c => c.AutoCompleteFilter);
            cm.MapMember(c => c.AutoCompleteLimit);
            cm.MapMember(c => c.AutoIncrementReference);
            cm.MapMember(c => c.AutoCompleteTexts).SetIgnoreIfDefault(true);
        });
        
        BsonClassMap.TryRegisterClassMap<CkEntityInheritance>(cm =>
        {
            cm.SetIgnoreExtraElements(true);
            cm.MapIdMember(c => c.InheritanceId).SetSerializer(new OctoObjectIdSerializer()).SetIdGenerator(new OctoObjectIdGenerator());
            cm.MapMember(c => c.ScopeId).SetIsRequired(true);
            cm.MapMember(c => c.OriginCkId).SetIsRequired(true);
            cm.MapMember(c => c.TargetCkId).SetIsRequired(true);
        });
        
        BsonClassMap.TryRegisterClassMap<CkEntityIndex>(cm =>
        {
            cm.SetIgnoreExtraElements(true);
            cm.MapMember(c => c.Language).SetIgnoreIfDefault(true);
        });

        BsonClassMap.TryRegisterClassMap<CkIndexFields>(cm =>
        {
            cm.SetIgnoreExtraElements(true);
            cm.MapMember(c => c.Weight).SetIgnoreIfDefault(true);
        });
        
        BsonClassMap.TryRegisterClassMap<AutoCompleteText>(cm =>
        {
            cm.SetIgnoreExtraElements(true);
            cm.MapMember(c => c.OccurrenceCount).SetElementName("count");
            cm.MapMember(c => c.Text).SetElementName("_id");
        });
        
        BsonClassMap.TryRegisterClassMap<RtEntity>(cm =>
        {
            cm.SetIgnoreExtraElements(true);
            cm.MapField("_attributes").SetElementName(Constants.AttributesName).SetSerializer(new RtAttributeDictionarySerializer());
            cm.MapIdMember(c => c.RtId).SetSerializer(new OctoObjectIdSerializer()).SetIdGenerator(new OctoObjectIdGenerator());
            cm.MapMember(c => c.RtCreationDateTime).SetIsRequired(true);
            cm.MapMember(c => c.RtChangedDateTime).SetIsRequired(true);
            cm.MapMember(c => c.CkId).SetIsRequired(true);
            cm.MapMember(c => c.RtWellKnownName).SetIgnoreIfDefault(true);
        });

        BsonClassMap.TryRegisterClassMap<RtAssociation>(cm =>
        {
            cm.SetIgnoreExtraElements(true);
            cm.MapIdMember(c => c.AssociationId).SetSerializer(new OctoObjectIdSerializer()).SetIdGenerator(new OctoObjectIdGenerator());
            cm.MapMember(c => c.AssociationRoleId).SetIsRequired(true);
            cm.MapMember(c => c.OriginCkId).SetIsRequired(true);
            cm.MapMember(c => c.OriginRtId).SetIsRequired(true);
            cm.MapMember(c => c.TargetCkId).SetIsRequired(true);
            cm.MapMember(c => c.TargetRtId).SetIsRequired(true);
        });
    }

    public async Task<bool> IsRepositoryExistingAsync(string name)
    {
        var databaseNames = await _client.ListDatabaseNamesAsync();

        return databaseNames.ToList().Any(x => string.Compare(x, name,
            StringComparison.InvariantCultureIgnoreCase) == 0);
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

        await _client.DropDatabaseAsync(name);
    }

    public IRepository GetRepository(string name)
    {
        ArgumentValidation.ValidateString(nameof(name), name);

        return new MongoRepository(_client.GetDatabase(name));
    }

    public async Task CreateUser(string authenticationDatabaseName, string databaseName,
        string user,
        string? password)
    {
        ArgumentValidation.ValidateString(nameof(authenticationDatabaseName), authenticationDatabaseName);
        ArgumentValidation.ValidateString(nameof(databaseName), databaseName);
        ArgumentValidation.ValidateString(nameof(user), user);
        ArgumentValidation.ValidateString(nameof(password), password);

        var database = _client.GetDatabase(authenticationDatabaseName);

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

    private void ConfigureMongoDriver()
    {
        // Remove convention first to avoid duplications
        // this call of Remove method makes no errors if occurs before any Register method call
        ConventionRegistry.Remove(OctoConventionCamelCase);
        ConventionRegistry.Remove(OctoConventionSerialization);
        
        // Register convention
        ConventionRegistry.Register(OctoConventionCamelCase, new ConventionPack
        {
            new CamelCaseElementNameConvention()
        }, _ => true);
        // This convention is needed to ensure that properties of a derived class of RtEntity
        // are not serialized.
        ConventionRegistry.Register(OctoConventionSerialization, new ConventionPack
        {
            new RtEntitySerializationConvention()
        }, t => typeof(RtEntity).IsAssignableFrom(t));
    }
}