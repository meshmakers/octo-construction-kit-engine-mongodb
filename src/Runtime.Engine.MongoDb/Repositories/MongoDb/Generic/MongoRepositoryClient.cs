using System.Diagnostics;
using Meshmakers.Common.Shared;
using Meshmakers.Octo.ConstructionKit.Contracts;
using Meshmakers.Octo.ConstructionKit.Contracts.Services;
using Meshmakers.Octo.Runtime.Contracts;
using Meshmakers.Octo.Runtime.Contracts.MongoDb;
using Meshmakers.Octo.Runtime.Contracts.MongoDb.Configuration;
using Meshmakers.Octo.Runtime.Contracts.MongoDb.Repositories;
using Meshmakers.Octo.Runtime.Contracts.MongoDb.Repositories.Entities;
using Meshmakers.Octo.Runtime.Contracts.Repositories;
using Meshmakers.Octo.Runtime.Contracts.RepositoryEntities;
using Meshmakers.Octo.Runtime.Engine.MongoDb.Repositories.Entities;
using Meshmakers.Octo.Runtime.Engine.MongoDb.Serialization;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using MongoDB.Bson;
using MongoDB.Bson.Serialization;
using MongoDB.Bson.Serialization.Conventions;
using MongoDB.Bson.Serialization.IdGenerators;
using MongoDB.Bson.Serialization.Serializers;
using MongoDB.Driver;
using MongoDB.Driver.Core.Events;
using MongoDB.Driver.GeoJsonObjectModel;
using DateTimeOffsetSerializer = Meshmakers.Octo.Runtime.Engine.MongoDb.Serialization.DateTimeOffsetSerializer;

namespace Meshmakers.Octo.Runtime.Engine.MongoDb.Repositories.MongoDb.Generic;

[DebuggerDisplay("{" + nameof(_instanceId) + "}")]
public abstract class MongoRepositoryClient : IRepositoryClient
{
    private const string OctoConventionCamelCase = "octo-convention-camelCase";
    private const string OctoRtEntityConvention = "octo-convention-rtEntity";
    private const string OctoRtRecordConvention = "octo-convention-rtRecord";

    private static volatile bool _isRegistered;
    private static volatile bool _isSerializerRegistered;
    private static readonly Lock ObjectIdLock = new();

    private readonly ILogger<MongoRepositoryClient> _logger;
    protected readonly Guid _instanceId = Guid.NewGuid();
    protected readonly IServiceProvider _serviceProvider;
    protected readonly IOptions<OctoSystemConfiguration> _systemConfiguration;
    private MongoClient? _client;


    protected MongoRepositoryClient(ILogger<MongoRepositoryClient> logger,
        IOptions<OctoSystemConfiguration> systemConfiguration,
        IServiceProvider serviceProvider)
    {
        _logger = logger;
        _systemConfiguration = systemConfiguration;
        _serviceProvider = serviceProvider;
        ArgumentValidation.ValidateString(nameof(systemConfiguration.Value.DatabaseHost),
            systemConfiguration.Value.DatabaseHost);

        ConfigureMongoDriver(_serviceProvider);
    }

    /// <summary>
    ///     Returns the mongodb client
    /// </summary>
    protected MongoClient Client
    {
        get
        {
            if (_client == null)
            {
                var mongoUrl = CreateConnectionUri();
                var settings = MongoClientSettings.FromUrl(mongoUrl);
                settings.ReadConcern = ReadConcern.Majority;
                settings.WriteConcern = new WriteConcern(WriteConcern.WMode.Majority, TimeSpan.FromSeconds(30));

                // Retry always writes to prevent
                // Write conflict during plan execution and yielding is disabled. :: Please retry your operation or multi-document transaction. 
                settings.RetryWrites = true;
                settings.ClusterConfigurator = cb =>
                {
                    cb.Subscribe<CommandStartedEvent>(e =>
                    {
                        _logger.LogDebug("{ObjCommandName} - {Json}", e.CommandName, e.Command.ToJson());
                    });
                    cb.Subscribe<ConnectionOpenedEvent>(e =>
                    {
                        _logger.LogDebug("Connection opened: {ConnectionId}", e.ConnectionId);
                    });
                    cb.Subscribe<ConnectionClosedEvent>(e =>
                    {
                        _logger.LogDebug("Connection closed: {ConnectionId}", e.ConnectionId);
                    });
                };
                _client = new MongoClient(settings);
            }

            return _client;
        }
    }

    public async Task<IOctoSession> GetSessionAsync()
    {
        var session = await Client.StartSessionAsync();
        var logger = _serviceProvider.GetRequiredService<ILogger<OctoUserSession>>();
        return new OctoUserSession(logger, session, Client.Settings.ApplicationName);
    }

    public IOctoSession GetSession()
    {
        var session = Client.StartSession();
        var logger = _serviceProvider.GetRequiredService<ILogger<OctoUserSession>>();
        return new OctoUserSession(logger, session, Client.Settings.ApplicationName);
    }

    public IRepository GetRepository(string name)
    {
        ArgumentValidation.ValidateString(nameof(name), name);

        var loggerFactory = _serviceProvider.GetRequiredService<ILoggerFactory>();
        return new MongoRepository(loggerFactory, Client.GetDatabase(name));
    }

    public void Dispose()
    {
        Client.Cluster.Dispose();
    }

    /// <summary>
    ///     Creates a connection uri for the mongodb client
    /// </summary>
    /// <returns></returns>
    protected abstract MongoUrl CreateConnectionUri();


    private static void ConfigureMongoDriver(IServiceProvider serviceProvider)
    {
        if (_isRegistered)
        {
            return;
        }

        lock (ObjectIdLock)
        {
            if (_isRegistered)
            {
                return;
            }

            // Remove convention first to avoid duplications
            // this call of Remove method makes no errors if occurs before any Register method call
            ConventionRegistry.Remove(OctoConventionCamelCase);
            ConventionRegistry.Remove(OctoRtEntityConvention);
            ConventionRegistry.Remove(OctoRtRecordConvention);

            // Register convention
            ConventionRegistry.Register(OctoConventionCamelCase, new ConventionPack
            {
                new CamelCaseElementNameConvention()
            }, _ => true);
            
            // Ensure that class maps are registered after generic conventions!
            // Otherwise, for example, CamelCaseElementName is not executed during mapping.
            // The position must be before class mapping registrations using conventions
            // here
            RegisterClassMaps();

            // This convention is needed to ensure that properties of a derived class of RtEntity
            // are not serialized and the correct polymorphic type is used.
            ConventionRegistry.Register(OctoRtEntityConvention, new ConventionPack
            {
                new RtEntityMapConvention(serviceProvider.GetRequiredService<ICkClassMappingService>())
            }, t => typeof(RtEntity).IsAssignableFrom(t));

            // This convention is needed to ensure that properties of a derived class of RtRecord
            // are not serialized and the correct polymorphic type is used.
            ConventionRegistry.Register(OctoRtRecordConvention, new ConventionPack
            {
                new RtRecordMapConvention(serviceProvider.GetRequiredService<ICkClassMappingService>())
            }, t => typeof(RtRecord).IsAssignableFrom(t));

            _isRegistered = true;
        }
    }

    internal static void RegisterSerializers()
    {
        if (_isSerializerRegistered)
        {
            return;
        }

        lock (ObjectIdLock)
        {
            if (_isSerializerRegistered)
            {
                return;
            }

            BsonSerializer.RegisterDiscriminatorConvention(typeof(object), new RtEntityDiscriminatorConvention("_t"));
            BsonSerializer.RegisterDiscriminatorConvention(typeof(RtEntity), new RtEntityDiscriminatorConvention("_t"));

            BsonSerializer.RegisterSerializer(new OctoObjectListSerializer());
            var objectSerializer = new OctoObjectSerializer(type => ObjectSerializer.DefaultAllowedTypes(type) ||
                                                                    type.FullName?.StartsWith(typeof(RtEntity)
                                                                        .Namespace!) ==
                                                                    true || type.Namespace!.StartsWith(typeof(GeoJson)
                                                                        .Namespace!)
                                                                    || type.Namespace!.StartsWith(typeof(CkModelId)
                                                                        .Namespace!) || type == typeof(List<RtRecord>));
            BsonSerializer.RegisterSerializer(objectSerializer);

            BsonSerializer.RegisterSerializer(new OctoObjectIdSerializer());
            BsonSerializer.RegisterDiscriminator(typeof(DateTimeOffset), "datetimeoffset");
            BsonSerializer.RegisterSerializer(new DateTimeOffsetSerializer());

            BsonSerializer.RegisterSerializer(new CkIdSerializer<CkTypeId, OctoTypeIdSerializer>());
            BsonSerializer.RegisterSerializer(new CkIdSerializer<CkAttributeId, OctoAttributeIdSerializer>());
            BsonSerializer.RegisterSerializer(
                new CkIdSerializer<CkAssociationRoleId, OctoAssociationIdSerializer>());
            BsonSerializer.RegisterSerializer(new CkIdSerializer<CkRecordId, OctoRecordIdSerializer>());
            BsonSerializer.RegisterSerializer(new CkIdSerializer<CkEnumId, OctoEnumIdSerializer>());
            BsonSerializer.RegisterSerializer(new ModelIdSerializer());

            _isSerializerRegistered = true;
        }
    }
    
    private static void RegisterClassMaps()
    {
        BsonClassMap.RegisterClassMap<CkModel>(cm =>
        {
            cm.SetIgnoreExtraElements(true);
            cm.MapIdMember(c => c.Id).SetIsRequired(true).SetIdGenerator(new NullIdChecker());
            cm.AutoMap();

            cm.GetMemberMap(c => c.Description).SetIgnoreIfDefault(true);
        });

        BsonClassMap.RegisterClassMap<CkType>(cm =>
        {
            cm.SetIgnoreExtraElements(true);
            cm.MapIdMember(c => c.CkTypeId).SetIsRequired(true).SetIdGenerator(new NullIdChecker());
            cm.AutoMap();

            cm.GetMemberMap(c => c.Description).SetIgnoreIfDefault(true);
            cm.GetMemberMap(c => c.IsFinal).SetIgnoreIfDefault(true);
            cm.GetMemberMap(c => c.IsAbstract).SetIgnoreIfDefault(true);
            cm.GetMemberMap(c => c.Attributes).SetIsRequired(true);
            cm.GetMemberMap(c => c.Indexes).SetIgnoreIfDefault(true);
            cm.GetMemberMap(c => c.EnableChangeStreamPreAndPostImages).SetIgnoreIfDefault(true);
        });

        BsonClassMap.RegisterClassMap<CkRecord>(cm =>
        {
            cm.SetIgnoreExtraElements(true);
            cm.MapIdMember(c => c.CkRecordId).SetIsRequired(true).SetIdGenerator(new NullIdChecker());
            cm.AutoMap();

            cm.GetMemberMap(c => c.Description).SetIgnoreIfDefault(true);
            cm.GetMemberMap(c => c.IsFinal).SetIsRequired(true);
            cm.GetMemberMap(c => c.IsAbstract).SetIgnoreIfDefault(true);
            cm.GetMemberMap(c => c.Attributes).SetIgnoreIfDefault(true);
        });

        BsonClassMap.RegisterClassMap<CkEnum>(cm =>
        {
            cm.SetIgnoreExtraElements(true);
            cm.MapIdMember(c => c.CkEnumId).SetIsRequired(true).SetIdGenerator(new NullIdChecker());
            cm.AutoMap();

            cm.GetMemberMap(c => c.Description).SetIgnoreIfDefault(true);
            cm.GetMemberMap(c => c.IsExtensible).SetIgnoreIfDefault(true);
            cm.GetMemberMap(c => c.Values).SetIsRequired(true);
        });

        BsonClassMap.RegisterClassMap<CkEnumValue>(cm =>
        {
            cm.SetIgnoreExtraElements(true);
            cm.AutoMap();

            cm.GetMemberMap(c => c.Key).SetIsRequired(true);
            cm.GetMemberMap(c => c.Name).SetIsRequired(true);
            cm.GetMemberMap(c => c.Description).SetIgnoreIfDefault(true);
            cm.GetMemberMap(c => c.IsExtension).SetIgnoreIfDefault(true);
        });

        BsonClassMap.RegisterClassMap<CkAttributeMetaData>(cm =>
        {
            cm.SetIgnoreExtraElements(true);
            cm.AutoMap();
            cm.GetMemberMap(c => c.Description).SetIgnoreIfDefault(true);
        });

        BsonClassMap.RegisterClassMap<CkAttribute>(cm =>
        {
            cm.SetIgnoreExtraElements(true);
            cm.MapIdMember(c => c.CkAttributeId).SetIsRequired(true).SetIdGenerator(new NullIdChecker());
            cm.AutoMap();

            cm.GetMemberMap(c => c.CkModelId).SetIsRequired(true);
            cm.GetMemberMap(c => c.AttributeValueType).SetIsRequired(true);
            cm.GetMemberMap(c => c.DefaultValues).SetIgnoreIfDefault(true);
            cm.GetMemberMap(c => c.Description).SetIgnoreIfDefault(true);
            cm.GetMemberMap(c => c.ValueCkEnumId).SetIgnoreIfDefault(true);
            cm.GetMemberMap(c => c.ValueCkRecordId).SetIgnoreIfDefault(true);
            cm.GetMemberMap(c => c.MetaData).SetIgnoreIfDefault(true);
            cm.GetMemberMap(c => c.IsDataStream).SetIgnoreIfDefault(true);
        });

        BsonClassMap.RegisterClassMap<CkAssociationRole>(cm =>
        {
            cm.SetIgnoreExtraElements(true);
            cm.MapIdMember(c => c.RoleId).SetIsRequired(true).SetIdGenerator(new NullIdChecker());
            cm.AutoMap();

            cm.GetMemberMap(c => c.Description).SetIgnoreIfDefault(true);
            cm.GetMemberMap(c => c.InboundMultiplicity).SetIsRequired(true);
            cm.GetMemberMap(c => c.InboundName).SetIgnoreIfDefault(true);
            cm.GetMemberMap(c => c.OutboundMultiplicity).SetIgnoreIfDefault(true);
            cm.GetMemberMap(c => c.OutboundName).SetIgnoreIfDefault(true);
        });

        BsonClassMap.RegisterClassMap<CkTypeAssociation>(cm =>
        {
            cm.SetIgnoreExtraElements(true);
            cm.MapIdMember(c => c.AssociationId).SetIdGenerator(new OctoObjectIdGenerator());
            cm.AutoMap();

            cm.GetMemberMap(c => c.RoleId).SetIsRequired(true);
            cm.GetMemberMap(c => c.OriginCkTypeId).SetIsRequired(true);
            cm.GetMemberMap(c => c.TargetCkTypeId).SetIsRequired(true);
        });

        BsonClassMap.RegisterClassMap<CkTypeAttribute>(cm =>
        {
            cm.SetIgnoreExtraElements(true);
            cm.MapIdMember(c => c.AttributeId).SetIsRequired(true).SetIdGenerator(new NullIdChecker());
            cm.AutoMap();

            cm.GetMemberMap(c => c.AttributeName).SetIsRequired(true);
            cm.GetMemberMap(c => c.AutoIncrementReference).SetIgnoreIfDefault(true);
            cm.GetMemberMap(c => c.AutoCompleteValues).SetIgnoreIfDefault(true);
        });

        BsonClassMap.RegisterClassMap<CkTypeInheritance>(cm =>
        {
            cm.SetIgnoreExtraElements(true);
            cm.MapIdMember(c => c.InheritanceId).SetIdGenerator(new OctoObjectIdGenerator());
            cm.AutoMap();

            cm.GetMemberMap(c => c.BaseCkTypeId).SetIsRequired(true);
            cm.GetMemberMap(c => c.InheritorCkTypeId).SetIsRequired(true);
        });

        BsonClassMap.RegisterClassMap<CkTypeIndex>(cm =>
        {
            cm.SetIgnoreExtraElements(true);
            cm.AutoMap();

            cm.GetMemberMap(c => c.IndexType).SetIgnoreIfDefault(true);
            cm.GetMemberMap(c => c.Fields).SetIgnoreIfDefault(true);
            cm.GetMemberMap(c => c.Language).SetIgnoreIfDefault(true);
        });

        BsonClassMap.RegisterClassMap<CkIndexFields>(cm =>
        {
            cm.SetIgnoreExtraElements(true);
            cm.AutoMap();

            cm.GetMemberMap(c => c.Weight).SetIgnoreIfDefault(true);
            cm.GetMemberMap(c => c.AttributeNames).SetIsRequired(true);
        });

        BsonClassMap.RegisterClassMap<CkTypeInfo>(cm =>
        {
            cm.SetIgnoreExtraElements(true);
            cm.AutoMap();

            cm.GetMemberMap(c => c.Inheritances).SetIsRequired(true);
            cm.GetMemberMap(c => c.InheritedTypes).SetIsRequired(true);
        });

        BsonClassMap.RegisterClassMap<CkInheritedTypeInfo>(cm =>
        {
            cm.SetIgnoreExtraElements(true);
            cm.AutoMap();

            cm.GetMemberMap(c => c.BaseTypeDepthIndex).SetIsRequired(true);
        });

        BsonClassMap.RegisterClassMap<AutoCompleteText>(cm =>
        {
            cm.SetIgnoreExtraElements(true);
            cm.AutoMap();

            cm.GetMemberMap(c => c.OccurrenceCount);
            cm.GetMemberMap(c => c.Text);
        });

        var isRegisterSuccessful = BsonClassMap.TryRegisterClassMap<RtTypeWithAttributes>(cm =>
        {
            cm.SetIgnoreExtraElements(true);
            cm.MapField("_attributes").SetElementName(Constants.AttributesName)
            .SetSerializer(new RtAttributeDictionarySerializer());
        });

        if (!isRegisterSuccessful)
        {
            throw TenantException.CannotRegisterBecauseAlreadyRegistered(typeof(RtTypeWithAttributes));
        }

        BsonClassMap.RegisterClassMap<RtEntity>(cm =>
        {
            cm.SetIsRootClass(true);
            cm.SetIgnoreExtraElements(true);
            cm.MapIdMember(c => c.RtId).SetIdGenerator(new OctoObjectIdGenerator());
            cm.AutoMap();

            cm.MapMember(c => c.RtCreationDateTime).SetIsRequired(true);
            cm.MapMember(c => c.RtChangedDateTime).SetIsRequired(true);
            cm.MapMember(c => c.CkTypeId).SetIsRequired(true);
            cm.MapMember(c => c.RtWellKnownName).SetIgnoreIfDefault(true);
        });

        BsonClassMap.RegisterClassMap<RtRecord>(cm =>
        {
            cm.SetIsRootClass(true);
            cm.SetIgnoreExtraElements(true);

            cm.MapMember(c => c.CkRecordId).SetElementName(nameof(RtRecord.CkRecordId).ToCamelCase())
                .SetIsRequired(true);
        });

        BsonClassMap.RegisterClassMap<RtAssociation>(cm =>
        {
            cm.SetIgnoreExtraElements(true);
            cm.MapIdMember(c => c.AssociationId).SetIdGenerator(new OctoObjectIdGenerator());
            cm.AutoMap();

            cm.GetMemberMap(c => c.AssociationRoleId).SetIsRequired(true);
            cm.GetMemberMap(c => c.OriginCkTypeId).SetIsRequired(true);
            cm.GetMemberMap(c => c.OriginRtId).SetIsRequired(true);
            cm.GetMemberMap(c => c.TargetCkTypeId).SetIsRequired(true);
            cm.GetMemberMap(c => c.TargetRtId).SetIsRequired(true);
            cm.GetMemberMap(c => c.TargetCkAttributeIds).SetIgnoreIfDefault(true);
        });

        BsonClassMap.RegisterClassMap<RtDeepGraphQueryResult>(cm =>
        {
            cm.SetIgnoreExtraElements(true);
            cm.MapIdMember(c => c.Id).SetElementName("_id");
            cm.AutoMap();
            cm.GetMemberMap(c => c.Associations);
        });

        BsonClassMap.RegisterClassMap<RtDeepGraphAssociationQueryResult>(cm =>
        {
            cm.SetIgnoreExtraElements(true);
            cm.AutoMap();

            cm.GetMemberMap(c => c.AssociationId);
            cm.GetMemberMap(c => c.AssociationRoleId);
            cm.GetMemberMap(c => c.TargetRtId);
            cm.GetMemberMap(c => c.TargetCkTypeId);
        });

        BsonClassMap.RegisterClassMap<RtEntityGraphItem>(cm =>
        {
            cm.SetIgnoreExtraElements(true);
            cm.AutoMap();

            cm.GetMemberMap(c => c.Associations).SetElementName(Constants.AssociationName);
        });

        BsonClassMap.RegisterClassMap<NavigationEnd>(cm =>
        {
            cm.SetIgnoreExtraElements(true);
            cm.MapIdMember(c => c.AssociationId).SetElementName("_id");
            cm.AutoMap();
        });

        BsonClassMap.RegisterClassMap<EntityBinaryInfo>(cm =>
        {
            cm.SetIgnoreExtraElements(true);
            cm.AutoMap();

            cm.GetMemberMap(c => c.Stream).SetShouldSerializeMethod(_=> false);
        });
    }
}
