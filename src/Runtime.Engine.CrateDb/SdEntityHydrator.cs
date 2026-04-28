using System.Reflection;
using Meshmakers.Octo.Runtime.Contracts.StreamData;

namespace Meshmakers.Octo.Runtime.Engine.CrateDb;

/// <summary>
///     Populates typed <see cref="SdEntity"/> subclasses from the untyped
///     <see cref="StreamDataRow"/> values that come back from the CrateDB repository.
///     Built-in fields (RtId, CkTypeId, Timestamp, RtWellKnownName, RtCreationDateTime,
///     RtChangedDateTime) are copied from the row's typed members. Data-stream attribute
///     values are copied into typed subclass properties via reflection; every value also
///     flows into the inherited <c>Attributes</c> bag so unknown keys remain accessible.
/// </summary>
public static class SdEntityHydrator
{
    private static readonly Dictionary<Type, PropertyInfo[]> PropertyCache = new();
    private static readonly object PropertyCacheLock = new();

    /// <summary>
    ///     Creates a new <typeparamref name="TEntity"/> populated from <paramref name="row"/>.
    /// </summary>
    public static TEntity Hydrate<TEntity>(StreamDataRow row)
        where TEntity : SdEntity, new()
    {
        var entity = new TEntity
        {
            RtId = row.RtId ?? default,
            CkTypeId = row.CkTypeId
                ?? throw new ArgumentException("StreamDataRow.CkTypeId must not be null", nameof(row)),
            Timestamp = row.Timestamp ?? default,
            RtWellKnownName = row.RtWellKnownName,
            RtCreationDateTime = row.RtCreationDateTime,
            RtChangedDateTime = row.RtChangedDateTime
        };

        // Copy every Value into the Attributes bag (keeps unknown keys reachable alongside typed props).
        foreach (var kvp in row.Values)
        {
            entity.SetAttributeRawValue(kvp.Key, kvp.Value);
        }

        // Map row.Values entries onto typed properties declared on the subclass.
        var props = GetCachedProperties(typeof(TEntity));
        foreach (var prop in props)
        {
            if (!row.Values.TryGetValue(prop.Name, out var value) || value == null) continue;

            var target = Nullable.GetUnderlyingType(prop.PropertyType) ?? prop.PropertyType;
            try
            {
                var converted = target.IsInstanceOfType(value)
                    ? value
                    : Convert.ChangeType(value, target);
                prop.SetValue(entity, converted);
            }
            catch (InvalidCastException)
            {
                // Leave as default; value stays accessible via Attributes
            }
            catch (FormatException)
            {
                // Likewise for parse-shaped mismatches
            }
        }

        return entity;
    }

    private static PropertyInfo[] GetCachedProperties(Type type)
    {
        lock (PropertyCacheLock)
        {
            if (!PropertyCache.TryGetValue(type, out var cached))
            {
                cached = type.GetProperties(BindingFlags.Instance | BindingFlags.Public | BindingFlags.DeclaredOnly)
                    .Where(p => p.CanWrite)
                    .ToArray();
                PropertyCache[type] = cached;
            }
            return cached;
        }
    }
}
