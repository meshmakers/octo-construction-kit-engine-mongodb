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
                var observability = new MongoCommandObservability(
                    _serviceProvider.GetRequiredService<ILogger<MongoCommandObservability>>(),
                    _serviceProvider.GetRequiredService<IOptionsMonitor<OctoSystemConfiguration>>(),
                    _serviceProvider.GetService<SlowQueriesBuffer>(),
                    _serviceProvider.GetService<SlowQueryExplainCache>());

                settings.ClusterConfigurator = cb =>
                {
                    cb.Subscribe<CommandStartedEvent>(e =>
                    {
                        observability.OnStarted(e);
                        _logger.LogDebug("{ObjCommandName} - {Json}", e.CommandName, e.Command.ToJson());
                    });
                    cb.Subscribe<CommandSucceededEvent>(observability.OnSucceeded);
                    cb.Subscribe<CommandFailedEvent>(observability.OnFailed);
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

                // Stage 2B — wire the live client into the observability listener so its
                // fire-and-forget explain dispatch has something to run commands against. The
                // ClusterConfigurator above already references `observability`, so we must
                // build the listener before the client, then set the client reference after.
                // Setter is idempotent; on the (unlikely) re-entrant Client-getter, the same
                // instance is written twice — harmless.
                observability.SetMongoClient(_client);
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

            // Register convention. The IgnoreExtraElementsConvention applies to ALL types and
            // makes the deserializer silently skip BSON elements that have no matching property —
            // mandatory for forward-compat across schema evolution (e.g. when a property is
            // removed from a CK metadata class but legacy documents in the database still carry it).
            ConventionRegistry.Register(OctoConventionCamelCase,
                new ConventionPack
                {
                    new CamelCaseElementNameConvention(),
                    new IgnoreExtraElementsConvention(true)
                }, _ => true);

            // Ensure that class maps are registered after generic conventions!
            // Otherwise, for example, CamelCaseElementName is not executed during mapping.
            // The position must be before class mapping registrations using conventions
            // here
            RegisterClassMaps();

            // This convention is needed to ensure that properties of a derived class of RtEntity
            // are not serialized and the correct polymorphic type is used.
            ConventionRegistry.Register(OctoRtEntityConvention,
                new ConventionPack
                {
                    new RtEntityMapConvention(serviceProvider.GetRequiredService<ICkClassMappingService>())
                }, t => typeof(RtEntity).IsAssignableFrom(t));

            // This convention is needed to ensure that properties of a derived class of RtRecord
            // are not serialized and the correct polymorphic type is used.
            ConventionRegistry.Register(OctoRtRecordConvention,
                new ConventionPack
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
            BsonSerializer.RegisterSerializer(new Serialization.TimeSpanSerializer());

            BsonSerializer.RegisterSerializer(new CkIdSerializer<CkTypeId, OctoTypeIdSerializer>());
            BsonSerializer.RegisterSerializer(new CkIdSerializer<CkAttributeId, OctoAttributeIdSerializer>());
            BsonSerializer.RegisterSerializer(
                new CkIdSerializer<CkAssociationRoleId, OctoAssociationIdSerializer>());
            BsonSerializer.RegisterSerializer(new CkIdSerializer<CkRecordId, OctoRecordIdSerializer>());
            BsonSerializer.RegisterSerializer(new CkIdSerializer<CkEnumId, OctoEnumIdSerializer>());
            BsonSerializer.RegisterSerializer(new ModelIdSerializer());

            // RtId serializers
            BsonSerializer.RegisterSerializer(new RtCkIdSerializer<CkTypeId, OctoTypeIdSerializer>());
            BsonSerializer.RegisterSerializer(new RtCkIdSerializer<CkRecordId, OctoRecordIdSerializer>());
            BsonSerializer.RegisterSerializer(
                new RtCkIdSerializer<CkAssociationRoleId, OctoAssociationIdSerializer>());

            _isSerializerRegistered = true;
        }
    }

    private static void RegisterClassMaps()
    {
        BsonClassMap.RegisterClassMap<SysLock>(cm =>
        {
            cm.SetIgnoreExtraElements(true);
            cm.MapIdMember(c => c.Id).SetIsRequired(true).SetIdGenerator(new NullIdChecker());
            cm.AutoMap();

            cm.MapMember(c => c.CreationDateTime).SetIsRequired(true);

            // MongoDB v3 driver requires an explicit GuidRepresentation per member —
            // the default Unspecified throws on serialize. Standard (UUID v4 layout) is
            // the recommended representation for new fields.
            cm.MapMember(c => c.OwnerToken).SetSerializer(
                new NullableSerializer<Guid>(new GuidSerializer(GuidRepresentation.Standard)));
        });

        BsonClassMap.RegisterClassMap<CkModel>(cm =>
        {
            cm.SetIgnoreExtraElements(true);
            cm.MapIdMember(c => c.Id).SetIsRequired(true).SetIdGenerator(new NullIdChecker());
            cm.AutoMap();

            cm.MapMember(c => c.Description).SetIgnoreIfDefault(true);
        });

        BsonClassMap.RegisterClassMap<CkType>(cm =>
        {
            cm.SetIgnoreExtraElements(true);
            cm.MapIdMember(c => c.CkTypeId).SetIsRequired(true).SetIdGenerator(new NullIdChecker());
            cm.AutoMap();

            cm.MapMember(c => c.Description).SetIgnoreIfDefault(true);
            cm.MapMember(c => c.IsFinal).SetIgnoreIfDefault(true);
            cm.MapMember(c => c.IsAbstract).SetIgnoreIfDefault(true);
            cm.MapMember(c => c.Attributes).SetIsRequired(true);
            cm.MapMember(c => c.Indexes).SetIgnoreIfDefault(true);
            cm.MapMember(c => c.EnableChangeStreamPreAndPostImages).SetIgnoreIfDefault(true);
        });

        BsonClassMap.RegisterClassMap<CkRecord>(cm =>
        {
            cm.SetIgnoreExtraElements(true);
            cm.MapIdMember(c => c.CkRecordId).SetIsRequired(true).SetIdGenerator(new NullIdChecker());
            cm.AutoMap();

            cm.MapMember(c => c.Description).SetIgnoreIfDefault(true);
            cm.MapMember(c => c.IsFinal).SetIsRequired(true);
            cm.MapMember(c => c.IsAbstract).SetIgnoreIfDefault(true);
            cm.MapMember(c => c.Attributes).SetIgnoreIfDefault(true);
        });

        BsonClassMap.RegisterClassMap<CkEnum>(cm =>
        {
            cm.SetIgnoreExtraElements(true);
            cm.MapIdMember(c => c.CkEnumId).SetIsRequired(true).SetIdGenerator(new NullIdChecker());
            cm.AutoMap();

            cm.MapMember(c => c.Description).SetIgnoreIfDefault(true);
            cm.MapMember(c => c.IsExtensible).SetIgnoreIfDefault(true);
            cm.MapMember(c => c.Values).SetIsRequired(true);
        });

        BsonClassMap.RegisterClassMap<CkEnumValue>(cm =>
        {
            cm.SetIgnoreExtraElements(true);
            cm.AutoMap();

            cm.MapMember(c => c.Key).SetIsRequired(true);
            cm.MapMember(c => c.Name).SetIsRequired(true);
            cm.MapMember(c => c.Description).SetIgnoreIfDefault(true);
            cm.MapMember(c => c.IsExtension).SetIgnoreIfDefault(true);
        });

        BsonClassMap.RegisterClassMap<CkAttributeMetaData>(cm =>
        {
            cm.SetIgnoreExtraElements(true);
            cm.AutoMap();
            cm.MapMember(c => c.Description).SetIgnoreIfDefault(true);
        });

        BsonClassMap.RegisterClassMap<CkAttribute>(cm =>
        {
            cm.SetIgnoreExtraElements(true);
            cm.MapIdMember(c => c.CkAttributeId).SetIsRequired(true)
                .SetIdGenerator(new NullIdChecker());
            cm.AutoMap();

            cm.MapMember(c => c.CkModelId).SetIsRequired(true);
            cm.MapMember(c => c.AttributeValueType).SetIsRequired(true);
            cm.MapMember(c => c.DefaultValues).SetIgnoreIfDefault(true);
            cm.MapMember(c => c.Description).SetIgnoreIfDefault(true);
            cm.MapMember(c => c.ValueCkEnumId).SetIgnoreIfDefault(true);
            cm.MapMember(c => c.ValueCkRecordId).SetIgnoreIfDefault(true);
            cm.MapMember(c => c.MetaData).SetIgnoreIfDefault(true);
        });

        BsonClassMap.RegisterClassMap<CkAssociationRole>(cm =>
        {
            cm.SetIgnoreExtraElements(true);
            cm.MapIdMember(c => c.RoleId).SetIsRequired(true).SetIdGenerator(new NullIdChecker());
            cm.AutoMap();

            cm.MapMember(c => c.Description).SetIgnoreIfDefault(true);
            cm.MapMember(c => c.InboundMultiplicity).SetIsRequired(true);
            cm.MapMember(c => c.InboundName).SetIgnoreIfDefault(true);
            cm.MapMember(c => c.OutboundMultiplicity).SetIgnoreIfDefault(true);
            cm.MapMember(c => c.OutboundName).SetIgnoreIfDefault(true);
        });

        BsonClassMap.RegisterClassMap<CkTypeAssociation>(cm =>
        {
            cm.SetIgnoreExtraElements(true);
            cm.MapIdMember(c => c.AssociationId).SetIdGenerator(new OctoObjectIdGenerator());
            cm.AutoMap();

            cm.MapMember(c => c.RoleId).SetIsRequired(true);
            cm.MapMember(c => c.OriginCkTypeId).SetIsRequired(true);
            cm.MapMember(c => c.TargetCkTypeId).SetIsRequired(true);
        });

        BsonClassMap.RegisterClassMap<CkTypeAttribute>(cm =>
        {
            cm.SetIgnoreExtraElements(true);
            cm.MapIdMember(c => c.AttributeId).SetIsRequired(true)
                .SetIdGenerator(new NullIdChecker());
            cm.AutoMap();

            cm.MapMember(c => c.AttributeName).SetIsRequired(true);
            cm.MapMember(c => c.AutoIncrementReference).SetIgnoreIfDefault(true);
            cm.MapMember(c => c.AutoCompleteValues).SetIgnoreIfDefault(true);
        });

        BsonClassMap.RegisterClassMap<CkTypeInheritance>(cm =>
        {
            cm.SetIgnoreExtraElements(true);
            cm.MapIdMember(c => c.InheritanceId).SetIdGenerator(new OctoObjectIdGenerator());
            cm.AutoMap();

            cm.MapMember(c => c.BaseCkTypeId).SetIsRequired(true);
            cm.MapMember(c => c.InheritorCkTypeId).SetIsRequired(true);
        });

        BsonClassMap.RegisterClassMap<CkRecordInheritance>(cm =>
        {
            cm.SetIgnoreExtraElements(true);
            cm.MapIdMember(c => c.InheritanceId).SetIdGenerator(new OctoObjectIdGenerator());
            cm.AutoMap();

            cm.MapMember(c => c.BaseCkRecordId).SetIsRequired(true);
            cm.MapMember(c => c.InheritorCkRecordId).SetIsRequired(true);
        });

        BsonClassMap.RegisterClassMap<CkTypeIndex>(cm =>
        {
            cm.SetIgnoreExtraElements(true);
            cm.AutoMap();

            cm.MapMember(c => c.IndexType).SetIgnoreIfDefault(true);
            cm.MapMember(c => c.Fields).SetIgnoreIfDefault(true);
            cm.MapMember(c => c.Language).SetIgnoreIfDefault(true);
        });

        BsonClassMap.RegisterClassMap<CkIndexFields>(cm =>
        {
            cm.SetIgnoreExtraElements(true);
            cm.AutoMap();

            cm.MapMember(c => c.Weight).SetIgnoreIfDefault(true);
            cm.MapMember(c => c.AttributeNames).SetIsRequired(true);
        });

        BsonClassMap.RegisterClassMap<CkTypeInfo>(cm =>
        {
            cm.SetIgnoreExtraElements(true);
            cm.AutoMap();

            cm.MapMember(c => c.Inheritances).SetIsRequired(true);
            cm.MapMember(c => c.InheritedTypes).SetIsRequired(true);
        });

        BsonClassMap.RegisterClassMap<CkInheritedTypeInfo>(cm =>
        {
            cm.SetIgnoreExtraElements(true);
            cm.AutoMap();

            cm.MapMember(c => c.BaseTypeDepthIndex).SetIsRequired(true);
        });

        BsonClassMap.RegisterClassMap<AutoCompleteText>(cm =>
        {
            cm.SetIgnoreExtraElements(true);
            cm.AutoMap();

            cm.MapMember(c => c.OccurrenceCount);
            cm.MapMember(c => c.Text);
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
            cm.MapMember(c => c.RtArchivedDateTime).SetIgnoreIfDefault(true);
            cm.MapMember(c => c.RtState).SetIgnoreIfDefault(true);
        });

        BsonClassMap.RegisterClassMap<RtRecord>(cm =>
        {
            cm.SetIsRootClass(true);
            cm.SetIgnoreExtraElements(true);

            // Disable discriminator for RtRecord - the type is determined by CkRecordId, not by _t
            // This prevents writing _t for derived RtRecord types
            cm.SetDiscriminatorIsRequired(false);

            cm.MapMember(c => c.CkRecordId).SetElementName(nameof(RtRecord.CkRecordId).ToCamelCase())
                .SetIsRequired(true);
        });

        BsonClassMap.RegisterClassMap<RtAssociation>(cm =>
        {
            cm.SetIgnoreExtraElements(true);
            cm.MapIdMember(c => c.AssociationId).SetIdGenerator(new OctoObjectIdGenerator());
            cm.AutoMap();

            cm.MapMember(c => c.AssociationRoleId).SetIsRequired(true);
            cm.MapMember(c => c.OriginCkTypeId).SetIsRequired(true);
            cm.MapMember(c => c.OriginRtId).SetIsRequired(true);
            cm.MapMember(c => c.TargetCkTypeId).SetIsRequired(true);
            cm.MapMember(c => c.TargetRtId).SetIsRequired(true);
            cm.MapMember(c => c.TargetCkAttributeIds).SetIgnoreIfDefault(true);
            cm.MapMember(c => c.RtState).SetIgnoreIfDefault(true);
        });

        BsonClassMap.RegisterClassMap<RtDeepGraphQueryResult>(cm =>
        {
            cm.SetIgnoreExtraElements(true);
            cm.MapIdMember(c => c.Id).SetElementName("_id");
            cm.AutoMap();
            cm.MapMember(c => c.Associations);
        });

        BsonClassMap.RegisterClassMap<RtDeepGraphAssociationQueryResult>(cm =>
        {
            cm.SetIgnoreExtraElements(true);
            cm.AutoMap();

            cm.MapMember(c => c.AssociationId);
            cm.MapMember(c => c.AssociationRoleId);
            cm.MapMember(c => c.TargetRtId);
            cm.MapMember(c => c.TargetCkTypeId);
        });

        BsonClassMap.RegisterClassMap<RtEntityGraphItem>(cm =>
        {
            cm.SetIgnoreExtraElements(true);
            cm.AutoMap();

            cm.MapMember(c => c.Associations).SetElementName(Constants.AssociationName);
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

            cm.MapMember(c => c.Stream).SetShouldSerializeMethod(_ => false);
        });
    }
}
