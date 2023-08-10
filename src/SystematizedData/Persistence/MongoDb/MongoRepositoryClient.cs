using System;
using System.Diagnostics;
using System.Linq;
using System.Threading.Tasks;
using Meshmakers.Common.Shared;
using Meshmakers.Octo.Common.Shared;
using Meshmakers.Octo.SystematizedData.Persistence.DataAccess;
using Meshmakers.Octo.SystematizedData.Persistence.DatabaseEntities;
using MongoDB.Bson;
using MongoDB.Bson.Serialization;
using MongoDB.Bson.Serialization.Conventions;
using MongoDB.Bson.Serialization.IdGenerators;
using MongoDB.Driver;

namespace Meshmakers.Octo.SystematizedData.Persistence.MongoDb;

[DebuggerDisplay("{" + nameof(_instanceId) + "}")]
public class MongoRepositoryClient : IRepositoryClient
{
    private readonly MongoClient _client;
    private const string OctoConventionCamelCase ="octo-convention-camelCase";
    private const string OctoConventionSerialization ="octo-convention-serialization";
    private readonly Guid _instanceId = Guid.NewGuid();

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

        urlBuilder.ApplicationName = $"{_instanceId}-{urlBuilder.Username}";
        urlBuilder.UseTls = mongoConnectionOptions.UseTls;
        urlBuilder.AllowInsecureTls = mongoConnectionOptions.AllowInsecureTls;
        // TODO: It seams that secondary servers do not have any work. This seems not be possibly. Other solution?
        // urlBuilder.ReadPreference = ReadPreference.SecondaryPreferred; 

        ConfigureMongoDriver();
        MongoClientSettings settings = MongoClientSettings.FromUrl(urlBuilder.ToMongoUrl());
        settings.ReadConcern = ReadConcern.Majority;
        settings.WriteConcern = new WriteConcern(WriteConcern.WMode.Majority, TimeSpan.FromSeconds(2));
        _client = new MongoClient(urlBuilder.ToMongoUrl());

        RegisterClassMaps();
    }

    public static bool isRegistered = false;

    private static void RegisterClassMaps()
    {
        if (isRegistered)
        {
            return;
        }
        isRegistered = true;
        
        BsonSerializer.TryRegisterSerializer(new CkIdSerializer<CkTypeId, OctoTypeIdSerializer>());
        BsonSerializer.TryRegisterSerializer(new CkIdSerializer<CkAttributeId, OctoAttributeIdSerializer>());
        BsonSerializer.TryRegisterSerializer(new CkIdSerializer<CkAssociationRoleId, OctoAssociationIdSerializer>());
        BsonSerializer.TryRegisterSerializer(new OctoObjectIdSerializer());
        BsonSerializer.TryRegisterSerializer(new ModelIdSerializer());
        
        BsonClassMap.TryRegisterClassMap<DatabaseEntities.CkModel>(cm =>
        {
            cm.SetIgnoreExtraElements(true);
            cm.MapIdMember(c => c.Id).SetIsRequired(true).SetIdGenerator(new NullIdChecker());
            cm.AutoMap();
            //cm.MapMember(c => c.ScopeId).SetIsRequired(true);
            //cm.MapMember(c => c.Dependencies).SetIsRequired(true);
        });
        
        BsonClassMap.TryRegisterClassMap<CkEntity>(cm =>
        {
            cm.SetIgnoreExtraElements(true);
            cm.MapIdMember(c => c.CkTypeId).SetIsRequired(true).SetIdGenerator(new NullIdChecker());
            cm.AutoMap();

            cm.GetMemberMap(c => c.IsFinal).SetIsRequired(true);
            cm.GetMemberMap(c => c.IsAbstract).SetIsRequired(true);
            cm.GetMemberMap(c => c.Attributes).SetIsRequired(true);
            cm.GetMemberMap(c => c.Indexes).SetIgnoreIfDefault(true);
            cm.GetMemberMap(c => c.EnableChangeStreamPreAndPostImages).SetIgnoreIfDefault(true);
        });
        
        BsonClassMap.TryRegisterClassMap<CkAttribute>(cm =>
        {
            cm.SetIgnoreExtraElements(true);
            cm.MapIdMember(c => c.AttributeId).SetIsRequired(true).SetIdGenerator(new NullIdChecker());
            cm.AutoMap();

            cm.GetMemberMap(c => c.AttributeValueType).SetIsRequired(true);
            cm.GetMemberMap(c => c.DefaultValue).SetIgnoreIfDefault(true);
            cm.GetMemberMap(c => c.DefaultValues).SetIgnoreIfDefault(true);
            cm.GetMemberMap(c => c.SelectionValues).SetIgnoreIfDefault(true);
        });
        
        BsonClassMap.TryRegisterClassMap<CkAssociationRole>(cm =>
        {
            cm.SetIgnoreExtraElements(true);
            cm.MapIdMember(c => c.RoleId).SetIsRequired(true).SetIdGenerator(new NullIdChecker());
            cm.AutoMap();

            cm.GetMemberMap(c => c.InboundMultiplicity).SetIsRequired(true);
            cm.GetMemberMap(c => c.InboundName).SetIgnoreIfDefault(true);
            cm.GetMemberMap(c => c.OutboundMultiplicity).SetIgnoreIfDefault(true);
            cm.GetMemberMap(c => c.OutboundName).SetIgnoreIfDefault(true);
        });
        
        BsonClassMap.TryRegisterClassMap<CkEntityAssociation>(cm =>
        {
            cm.SetIgnoreExtraElements(true);
            cm.MapIdMember(c => c.AssociationId).SetIdGenerator(new OctoObjectIdGenerator());
            cm.AutoMap();

            cm.GetMemberMap(c => c.RoleId).SetIsRequired(true);
            cm.GetMemberMap(c => c.OriginCkTypeId).SetIsRequired(true);
            cm.GetMemberMap(c => c.TargetCkTypeId).SetIsRequired(true);
        });
        
        BsonClassMap.TryRegisterClassMap<CkEntityAttribute>(cm =>
        {
            cm.SetIgnoreExtraElements(true);
            cm.MapIdMember(c => c.AttributeId).SetIsRequired(true).SetIdGenerator(new NullIdChecker());
            cm.AutoMap();

            cm.GetMemberMap(c => c.AttributeName).SetIsRequired(true);
            cm.GetMemberMap(c => c.IsAutoCompleteEnabled).SetIgnoreIfDefault(true);
            cm.GetMemberMap(c => c.AutoCompleteFilter).SetIgnoreIfDefault(true);
            cm.GetMemberMap(c => c.AutoCompleteLimit).SetIgnoreIfDefault(true);
            cm.GetMemberMap(c => c.AutoIncrementReference).SetIgnoreIfDefault(true);
            cm.GetMemberMap(c => c.AutoCompleteTexts).SetIgnoreIfDefault(true);
        });
        
        BsonClassMap.TryRegisterClassMap<CkEntityInheritance>(cm =>
        {
            cm.SetIgnoreExtraElements(true);
            cm.MapIdMember(c => c.InheritanceId).SetIdGenerator(new OctoObjectIdGenerator());
            cm.AutoMap();

            cm.GetMemberMap(c => c.OriginCkTypeId).SetIsRequired(true);
            cm.GetMemberMap(c => c.TargetCkTypeId).SetIsRequired(true);
        });
        
        BsonClassMap.TryRegisterClassMap<CkEntityIndex>(cm =>
        {
            cm.SetIgnoreExtraElements(true);
            cm.AutoMap();

            cm.GetMemberMap(c => c.IndexType).SetIgnoreIfDefault(true);
            cm.GetMemberMap(c => c.Fields).SetIgnoreIfDefault(true);
            cm.GetMemberMap(c => c.Language).SetIgnoreIfDefault(true);
        });

        BsonClassMap.TryRegisterClassMap<CkIndexFields>(cm =>
        {
            cm.SetIgnoreExtraElements(true);
            cm.AutoMap();

            cm.GetMemberMap(c => c.Weight).SetIgnoreIfDefault(true);
            cm.GetMemberMap(c => c.AttributeNames).SetIsRequired(true);
        });
        
        BsonClassMap.TryRegisterClassMap<CkTypeInfo>(cm =>
        {
            cm.SetIgnoreExtraElements(true);
            cm.MapIdMember(c => c.CkTypeId).SetIdGenerator(new NullIdChecker()).SetIsRequired(true);
            cm.AutoMap();

            cm.GetMemberMap(c => c.TextSearchLanguages).SetIgnoreIfDefault(true);
            cm.GetMemberMap(c => c.BaseTypes).SetIsRequired(true);
            cm.GetMemberMap(c => c.Associations).SetIsRequired(true);
            cm.GetMemberMap(c => c.Attributes).SetIsRequired(true);
        });
        
        BsonClassMap.TryRegisterClassMap<CkBaseTypeInfo>(cm =>
        {
            cm.SetIgnoreExtraElements(true);
            cm.MapIdMember(c => c.InheritanceId).SetIdGenerator(new OctoObjectIdGenerator());
            cm.AutoMap();

            cm.GetMemberMap(c => c.OriginCkTypeId).SetIsRequired(true);
            cm.GetMemberMap(c => c.TargetCkTypeId).SetIsRequired(true);
            cm.GetMemberMap(c => c.BaseTypeDepthIndex).SetIsRequired(true);
        });
        
        BsonClassMap.TryRegisterClassMap<AutoCompleteText>(cm =>
        {
            cm.SetIgnoreExtraElements(true);
            cm.AutoMap();

            cm.GetMemberMap(c => c.OccurrenceCount);
            cm.GetMemberMap(c => c.Text);
        });
        
        BsonClassMap.TryRegisterClassMap<RtEntity>(cm =>
        {
            cm.SetIsRootClass(true);
            cm.SetIgnoreExtraElements(true);
            cm.MapField("_attributes").SetElementName(Constants.AttributesName).SetSerializer(new RtAttributeDictionarySerializer());
            cm.MapIdMember(c => c.RtId).SetIdGenerator(new OctoObjectIdGenerator());
            cm.MapMember(c => c.RtCreationDateTime).SetElementName(nameof(RtEntity.RtCreationDateTime).ToCamelCase()).SetIsRequired(true);
            cm.MapMember(c => c.RtChangedDateTime).SetElementName(nameof(RtEntity.RtChangedDateTime).ToCamelCase()).SetIsRequired(true);
            cm.MapMember(c => c.CkTypeId).SetElementName(nameof(RtEntity.CkTypeId).ToCamelCase()).SetIsRequired(true);
            cm.MapMember(c => c.RtWellKnownName).SetElementName(nameof(RtEntity.RtWellKnownName).ToCamelCase()).SetIgnoreIfDefault(true);
        });

        BsonClassMap.TryRegisterClassMap<RtAssociation>(cm =>
        {
            cm.SetIgnoreExtraElements(true);
            cm.MapIdMember(c => c.AssociationId).SetIdGenerator(new OctoObjectIdGenerator());
            cm.AutoMap();

            cm.GetMemberMap(c => c.AssociationRoleId).SetIsRequired(true);
            cm.GetMemberMap(c => c.OriginCkTypeId).SetIsRequired(true);
            cm.GetMemberMap(c => c.OriginRtId).SetIsRequired(true);
            cm.GetMemberMap(c => c.TargetCkTypeId).SetIsRequired(true);
            cm.GetMemberMap(c => c.TargetRtId).SetIsRequired(true);
        });
    }

    public async Task<bool> IsRepositoryExistingAsync(string name)
    {
        var databaseNames = await _client.ListDatabaseNamesAsync();

        return databaseNames.ToList().Any(x => string.Compare(x, name,
            StringComparison.InvariantCultureIgnoreCase) == 0);
    }
    
    
    public async Task<IOctoSession> StartSessionAsync()
    {
        var session = await _client.StartSessionAsync();
        return new OctoSession(session, _client.Settings.ApplicationName);
    }

    public IOctoSession StartSession()
    {
        var session = _client.StartSession();
        return new OctoSession(session, _client.Settings.ApplicationName);
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