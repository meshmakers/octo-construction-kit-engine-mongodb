using System.Diagnostics.CodeAnalysis;
using System.Runtime.CompilerServices;

using Meshmakers.Octo.Runtime.Engine.MongoDb.Repositories.MongoDb.Generic;

namespace Meshmakers.Octo.Runtime.Engine.MongoDb.Serialization;

/// <summary>
/// Registers OctoMesh's BSON serializers — most importantly the
/// <see cref="OctoObjectSerializer"/> for <c>typeof(object)</c> — at assembly load,
/// before any other code in this assembly can run.
///
/// Why a module initializer and not just the DI call in
/// <c>AddMongoDbRuntimeRepository()</c>:
///
/// The MongoDB driver lazily registers its own framework-only <c>ObjectSerializer</c> for
/// <c>typeof(object)</c> the first time anything looks one up — including indirectly via
/// <c>BsonClassMap.RegisterClassMap</c> on any class with an <c>object</c>-typed member
/// (e.g. the CK class maps pulled in by <c>AddCkModelTestV1()</c>). Once that default is
/// cached, <c>BsonSerializer.RegisterSerializer</c> throws "already registered" and our
/// allowed-types object serializer is silently dropped.
///
/// In a parallel xUnit run this is a genuine race: one fixture thread is mid-way through
/// <see cref="MongoRepositoryClient.RegisterSerializers"/> (the idempotency flag is set
/// eagerly to stop an unrelated cascade) while another thread triggers the lazy default
/// object-serializer registration. The default then wins, and every filter/update render
/// that boxes a custom type into <c>object</c> fails with
/// "Type … is not configured as a type that is allowed to be serialized" — recurring CI
/// failures (builds 36175 / 36256 / 36440 / 36992).
///
/// A module initializer runs exactly once, single-threaded, before any fixture constructor,
/// class-map registration, or <c>typeof(object)</c> lookup in this assembly — so our object
/// serializer is guaranteed to be the one cached for <c>typeof(object)</c>. The existing DI
/// call in <c>AddMongoDbRuntimeRepository()</c> remains (now a no-op via the idempotency
/// guard); production startup behaviour is unchanged apart from the registration happening
/// slightly earlier.
/// </summary>
internal static class BsonSerializationModuleInitializer
{
    [ModuleInitializer]
    [SuppressMessage("Usage", "CA2255:The 'ModuleInitializer' attribute should not be used in libraries",
        Justification =
            "Deliberate: the OctoMesh object serializer must win the typeof(object) registration " +
            "before any class-map registration or object lookup runs anywhere in the process. A module " +
            "initializer is the only hook that is guaranteed to run once, single-threaded, before all " +
            "other code in this assembly — which is exactly the ordering guarantee this fix needs.")]
    internal static void Initialize()
    {
        MongoRepositoryClient.RegisterSerializers();
    }
}
