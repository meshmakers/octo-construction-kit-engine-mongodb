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
                    _serviceProvider.GetService<SlowQueryExplainCache>(),
                    // Stage 2D — opt-in CK cache for CK-YAML emission in slow-query
                    // suggestions. Hosts that registered AddRuntimeEngine() have
                    // ICkCacheService in DI; bare engine-mongodb consumers don't, in which
                    // case the suggester silently falls back to MongoDB-only output.
                    _serviceProvider.GetService<Meshmakers.Octo.ConstructionKit.Contracts.Services.ICkCacheService>());

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

    /// <summary>
    /// Allowed-types predicate for the OctoMesh object serializer: the MongoDB-driver framework
    /// defaults plus the repository-entity / GeoJson / CK-id namespaces and <c>List&lt;RtRecord&gt;</c>.
    ///
    /// Shared by the process-global object serializer registered in <see cref="RegisterSerializers"/>
    /// AND by the explicit value serializer in
    /// <see cref="Serialization.RtAttributeDictionarySerializer"/>. The dictionary serializer pins
    /// its own object serializer so attribute values (notably <c>RtRecord</c>) deserialize correctly
    /// even when a process-global registration race leaves the MongoDB driver's default
    /// <c>ObjectSerializer</c> (framework-types only) registered for <c>typeof(object)</c> — the
    /// failure mode behind CI build 36440.
    /// </summary>
    internal static readonly Func<Type, bool> OctoObjectAllowedTypes = type =>
        ObjectSerializer.DefaultAllowedTypes(type) ||
        type.FullName?.StartsWith(typeof(RtEntity).Namespace!) == true ||
        type.Namespace?.StartsWith(typeof(GeoJson).Namespace!) == true ||
        type.Namespace?.StartsWith(typeof(CkModelId).Namespace!) == true ||
        type == typeof(List<RtRecord>);

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

            // Idempotency contract — set BEFORE the registrations, not after.
            //
            // BsonSerializer's static registry uses lazy default registration for
            // `typeof(object)`: the first call to `LookupDiscriminatorConvention(typeof(object))`
            // anywhere in the process (including indirectly via `BsonClassMap.RegisterClassMap`
            // on a CK class with object-typed members) auto-registers
            // `ObjectDiscriminatorConvention`. Our subsequent
            // `RegisterDiscriminatorConvention(typeof(object), ...)` then throws
            // "already registered" — and historically this left `_isSerializerRegistered`
            // FALSE, so every subsequent fixture ctor in the same test process re-entered
            // and re-threw, cascading 304+ test failures (CI builds 36175, 36256).
            //
            // Setting the flag eagerly + wrapping each registration in `TryRegister*` makes
            // the method idempotent across both the desired (single startup) path and the
            // CI race path. Production startup is unchanged (single thread, default state
            // is empty → all registrations succeed). The defensive catch only fires when
            // BSON state is pre-populated by a different path.
            _isSerializerRegistered = true;

            TryRegisterDiscriminatorConvention(typeof(object), RtEntityDiscriminatorConvention.Instance);
            TryRegisterDiscriminatorConvention(typeof(RtEntity), RtEntityDiscriminatorConvention.Instance);

            // RtRecord is its own discriminated root hierarchy (SetIsRootClass in RegisterClassMaps),
            // NOT a subtype of RtEntity, so the typeof(RtEntity) registration above does not cover it.
            // Register our convention for typeof(RtRecord) explicitly and early — this method runs from
            // the module initializer before any class map is frozen — so every generated CK record
            // subtype (RtUserLoginRecord, RtSecretRecord, …) inherits it instead of falling back to the
            // driver's default Hierarchical/Scalar convention. The default would write a _t discriminator
            // ARRAY like ["RtRecord","RtUserLoginRecord"] on save and then throw "Unknown discriminator
            // value 'RtUserLoginRecord'" on load — a silent, per-process registration race that broke
            // tenant-wide external / EntraID login (AB#4291).
            TryRegisterDiscriminatorConvention(typeof(RtRecord), RtEntityDiscriminatorConvention.Instance);

            TryRegisterSerializer(new OctoObjectListSerializer());
            var objectSerializer = new OctoObjectSerializer(OctoObjectAllowedTypes);
            TryRegisterSerializer(objectSerializer);

            // Fail fast if the driver's framework-only default ObjectSerializer won the
            // registration for typeof(object) (a lost race / wrong-order load). When that
            // happens TryRegisterSerializer above swallows the "already registered" throw and
            // the default stays cached — every later filter/update render that boxes a custom
            // type into object then fails with "Type … is not configured as a type that is
            // allowed to be serialized", but only on the handful of boxed-object paths, so it
            // reads as flaky (CI builds 36175 / 36256 / 36440 / 36992). The module initializer
            // in BsonSerializationModuleInitializer is what guarantees we win; this assertion
            // turns any future regression of that guarantee into one loud, named failure at
            // startup instead of a scattered, intermittent test failure.
            var activeObjectSerializer = BsonSerializer.LookupSerializer(typeof(object));
            if (activeObjectSerializer is not OctoObjectSerializer)
            {
                throw new InvalidOperationException(
                    $"The MongoDB driver's default '{activeObjectSerializer.GetType().FullName}' is registered " +
                    $"for typeof(object) instead of OctoObjectSerializer. A class map or object-typed lookup ran " +
                    $"before OctoMesh serializer registration. Ensure nothing touches BSON for typeof(object) " +
                    $"before {nameof(BsonSerializationModuleInitializer)} / {nameof(RegisterSerializers)}.");
            }

            // Same fail-fast guarantee for the DISCRIMINATOR CONVENTION as the object-serializer
            // assertion above. TryRegisterDiscriminatorConvention silently accepts a pre-existing
            // (driver-default) convention, so a lost registration race is otherwise invisible: the
            // service starts normally but every RtRecord subtype resolves to the driver's
            // Hierarchical/Scalar convention, writes a _t discriminator array, and throws
            // "Unknown discriminator value" on read — tenant-wide external-login failures (AB#4291).
            // Assert both the catch-all (object) and the record root (RtRecord) resolve to ours.
            AssertOctoDiscriminatorConvention(typeof(object));
            AssertOctoDiscriminatorConvention(typeof(RtRecord));

            TryRegisterSerializer(new OctoObjectIdSerializer());
            TryRegisterDiscriminator(typeof(DateTimeOffset), "datetimeoffset");
            TryRegisterSerializer(new DateTimeOffsetSerializer());
            TryRegisterSerializer(new Serialization.TimeSpanSerializer());

            TryRegisterSerializer(new CkIdSerializer<CkTypeId, OctoTypeIdSerializer>());
            TryRegisterSerializer(new CkIdSerializer<CkAttributeId, OctoAttributeIdSerializer>());
            TryRegisterSerializer(new CkIdSerializer<CkAssociationRoleId, OctoAssociationIdSerializer>());
            TryRegisterSerializer(new CkIdSerializer<CkRecordId, OctoRecordIdSerializer>());
            TryRegisterSerializer(new CkIdSerializer<CkEnumId, OctoEnumIdSerializer>());
            TryRegisterSerializer(new ModelIdSerializer());

            // RtId serializers
            TryRegisterSerializer(new RtCkIdSerializer<CkTypeId, OctoTypeIdSerializer>());
            TryRegisterSerializer(new RtCkIdSerializer<CkRecordId, OctoRecordIdSerializer>());
            TryRegisterSerializer(new RtCkIdSerializer<CkAssociationRoleId, OctoAssociationIdSerializer>());
        }
    }

    /// <summary>
    /// Idempotent wrapper around <see cref="BsonSerializer.RegisterSerializer{T}(IBsonSerializer{T})"/>.
    /// Same idempotency story as <see cref="TryRegisterDiscriminatorConvention"/>: the driver
    /// throws if the value type already has a serializer registered. Pre-registration can
    /// happen via CK class-map source generation that touches BSON state before
    /// <see cref="RegisterSerializers"/> runs.
    /// </summary>
    private static void TryRegisterSerializer<T>(IBsonSerializer<T> serializer)
    {
        try
        {
            BsonSerializer.RegisterSerializer(serializer);
        }
        catch (BsonSerializationException)
        {
            // Pre-registered — accept what's there.
        }
    }

    /// <summary>
    /// Idempotent wrapper around <see cref="BsonSerializer.RegisterDiscriminatorConvention"/>.
    /// Absorbs the "already registered" exception that the MongoDB driver throws when the
    /// registry already holds a convention for the given type — typically because an earlier
    /// path triggered the driver's lazy default-registration on first
    /// <c>LookupDiscriminatorConvention</c>. Our `RtEntity`-specific convention on
    /// <c>typeof(RtEntity)</c> still wins for polymorphic RtEntity deserialization because
    /// the driver walks the type hierarchy and finds the more specific match first; the
    /// `typeof(object)` registration is a defensive catch-all for List&lt;object&gt; cases.
    /// </summary>
    private static void TryRegisterDiscriminatorConvention(Type type, IDiscriminatorConvention convention)
    {
        try
        {
            BsonSerializer.RegisterDiscriminatorConvention(type, convention);
        }
        catch (BsonSerializationException)
        {
            // Pre-registered (e.g. lazy default from CK class-map source generation).
            // Accept what's there; production startup never hits this path.
        }
    }

    /// <summary>
    /// Startup guard mirroring the object-serializer assertion in <see cref="RegisterSerializers"/>.
    /// <see cref="TryRegisterDiscriminatorConvention"/> silently accepts a pre-existing (driver-default)
    /// convention, so a lost registration race is otherwise invisible: the service starts normally but
    /// every RtRecord subtype resolves to the driver's Hierarchical/Scalar convention, which writes a
    /// <c>_t</c> discriminator array and throws "Unknown discriminator value" on read — tenant-wide
    /// external-login failures (AB#4291). This turns that silent failure into one loud startup exception.
    /// </summary>
    private static void AssertOctoDiscriminatorConvention(Type type)
    {
        var convention = BsonSerializer.LookupDiscriminatorConvention(type);
        if (convention is not RtEntityDiscriminatorConvention)
        {
            throw new InvalidOperationException(
                $"The discriminator convention for '{type.FullName}' resolved to " +
                $"'{convention.GetType().FullName}' instead of {nameof(RtEntityDiscriminatorConvention)}. " +
                $"A class map or discriminator lookup ran before OctoMesh registration " +
                $"({nameof(BsonSerializationModuleInitializer)} / {nameof(RegisterSerializers)}); RtRecord " +
                $"subtypes would be written with a _t discriminator array and fail to deserialize (AB#4291).");
        }
    }

    /// <summary>
    /// Same idempotency story as <see cref="TryRegisterDiscriminatorConvention"/>, applied
    /// to the type-name discriminator registration. The MongoDB driver throws if the same
    /// type is given two distinct discriminator strings; here we accept the existing one.
    /// </summary>
    private static void TryRegisterDiscriminator(Type type, string discriminator)
    {
        try
        {
            BsonSerializer.RegisterDiscriminator(type, discriminator);
        }
        catch (BsonSerializationException)
        {
            // Pre-registered — accept what's there.
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
            // Pin our discriminator convention on the class map itself so it can never depend on the
            // typeof(object) registration winning a lookup-order race (AB#4291).
            cm.SetDiscriminatorConvention(RtEntityDiscriminatorConvention.Instance);
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

            // Pin our discriminator convention directly on the class map (belt-and-braces with the
            // early typeof(RtRecord) registration in RegisterSerializers). SetIsRootClass makes the
            // driver serialize a discriminator for subtypes; without our convention that default is a
            // Hierarchical _t array like ["RtRecord","RtUserLoginRecord"]. Ours returns null for
            // RtRecord types (the type is identified by ckRecordId, so no _t is written) and maps any
            // legacy *Record _t value back to RtRecord on read (AB#4291 / AB#3321).
            cm.SetDiscriminatorConvention(RtEntityDiscriminatorConvention.Instance);

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
