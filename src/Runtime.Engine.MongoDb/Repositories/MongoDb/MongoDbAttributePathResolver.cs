using System.Text;

using Meshmakers.Common.Shared;
using Meshmakers.Octo.ConstructionKit.Contracts;
using Meshmakers.Octo.ConstructionKit.Contracts.DataTransferObjects;
using Meshmakers.Octo.Runtime.Contracts;

namespace Meshmakers.Octo.Runtime.Engine.MongoDb.Repositories.MongoDb;

/// <summary>
/// Central utility for resolving CK attribute paths (e.g. "TimeRange.From") to
/// MongoDB field paths (e.g. "attributes.timeRange.attributes.from").
/// This is the single source of truth for path resolution, used by both
/// query field filters and index creation.
/// </summary>
internal static class MongoDbAttributePathResolver
{
    /// <summary>
    /// Resolves a CK attribute path to a fully qualified MongoDB field path.
    /// </summary>
    /// <param name="attributePath">The CK attribute path (e.g. "TimeRange.From")</param>
    /// <param name="provider">The metadata provider for attribute lookups</param>
    /// <returns>The MongoDB field path (e.g. "attributes.timeRange.attributes.from"), or null if the path is invalid</returns>
    public static string? ResolveToMongoDbFieldPath(string attributePath, IAttributeMetadataProvider provider)
    {
        var pathTerms = RtPathEvaluator.TokenizePath(attributePath);

        var sb = new StringBuilder();
        sb.Append(Constants.AttributesName);

        var currentProvider = provider;
        AttributeValueTypesDto lastValueType = default;

        foreach (var pathTerm in pathTerms)
        {
            switch (pathTerm.Type)
            {
                case PathType.Attribute:
                    if (currentProvider.TryGetAttribute(pathTerm.Value.ToPascalCase(), out var valueType))
                    {
                        if (currentProvider.IsRecordContext)
                        {
                            sb.Append(Constants.PathSeparator);
                            sb.Append(Constants.AttributesName);
                        }

                        sb.Append(Constants.PathSeparator);
                        sb.Append(pathTerm.Value.ToCamelCase());

                        lastValueType = valueType;

                        if (valueType is AttributeValueTypesDto.Record or AttributeValueTypesDto.RecordArray)
                        {
                            var recordProvider = currentProvider.NavigateToRecord(pathTerm.Value.ToPascalCase());
                            if (recordProvider == null)
                            {
                                return null;
                            }

                            currentProvider = recordProvider;
                        }
                    }
                    else
                    {
                        return null;
                    }

                    break;

                case PathType.ArrayIndex:
                    if (lastValueType is AttributeValueTypesDto.StringArray
                        or AttributeValueTypesDto.IntArray
                        or AttributeValueTypesDto.RecordArray)
                    {
                        if (pathTerm.Value != "*")
                        {
                            sb.Append(string.Format(Constants.IndexAccessor, pathTerm.Value));
                        }
                    }
                    else
                    {
                        return null;
                    }

                    break;

                default:
                    return null;
            }
        }

        return sb.ToString();
    }

    /// <summary>
    /// Mirror of <see cref="ResolveToMongoDbFieldPath"/>: takes a MongoDB field path produced
    /// by the forward function (e.g. <c>attributes.name.value</c> or
    /// <c>attributes.timeRange.attributes.from.value</c>) and walks it back to the
    /// PascalCase CK attribute path (<c>Name</c> / <c>TimeRange.From</c>).
    /// </summary>
    /// <remarks>
    /// <para>
    /// Algorithm (the strict inverse of the forward function):
    /// </para>
    /// <list type="number">
    ///   <item>Strip the leading <c>attributes.</c> prefix. If absent, the path is not a CK
    ///         attribute (e.g. <c>ckTypeId.fullName</c>, <c>_id</c>, <c>rtId</c>) and we
    ///         return null — Stage 2D consciously skips CK-YAML emission for those paths.</item>
    ///   <item>Strip the trailing <c>.value</c> suffix. The forward function does not append it
    ///         itself (the slow-query buffer captures the path with <c>.value</c> appended by
    ///         the downstream filter builder), but a robust reverse function must accept both
    ///         shapes — with-suffix from a real BSON filter, without-suffix when the suggester
    ///         hands us a freshly-built path.</item>
    ///   <item>Tokenize the remaining segments by <c>.</c>. The forward function alternates
    ///         attribute-name segments with <c>attributes</c> separators on every record hop,
    ///         so the rule is: segments at indices 0, 2, 4, … are attribute camelCase names;
    ///         segments at odd indices are the literal <c>attributes</c> marker. Reject any
    ///         odd-index segment that isn't <c>attributes</c>.</item>
    ///   <item>For each attribute segment: convert camelCase → PascalCase (the inverse of
    ///         <c>.ToCamelCase()</c>). Look up via <c>provider.TryGetAttribute</c>. If the
    ///         attribute is a Record/RecordArray, navigate into it via
    ///         <c>provider.NavigateToRecord</c> and continue. If unknown at any segment,
    ///         return null — the path didn't match the CK shape and the caller should not
    ///         emit a CK-YAML snippet for it.</item>
    /// </list>
    /// </remarks>
    /// <param name="mongoFieldPath">The MongoDB field path (e.g. <c>attributes.name.value</c>).</param>
    /// <param name="provider">The metadata provider for attribute lookups (typically a
    /// <see cref="CkCacheAttributeMetadataProvider"/> scoped to the originating CK type).</param>
    /// <returns>The PascalCase CK attribute path (e.g. <c>Name</c> / <c>TimeRange.From</c>),
    /// or null when the path is not a resolvable CK attribute.</returns>
    public static string? TryReverseToCkPath(string mongoFieldPath, IAttributeMetadataProvider provider)
    {
        if (string.IsNullOrEmpty(mongoFieldPath))
        {
            return null;
        }

        var attributesPrefix = Constants.AttributesName + Constants.PathSeparator;
        if (!mongoFieldPath.StartsWith(attributesPrefix, StringComparison.Ordinal))
        {
            return null;
        }

        var trimmed = mongoFieldPath.Substring(attributesPrefix.Length);

        // The forward function does not emit ".value" — that suffix is added downstream by the
        // BSON filter builder when targeting a scalar. We accept paths with or without it so
        // the reverse function works against both a freshly-built path and a captured filter
        // path. Trailing ".value" with nothing in front is rejected by the empty-token check
        // a few lines below.
        const string valueSuffix = ".value";
        if (trimmed.EndsWith(valueSuffix, StringComparison.Ordinal))
        {
            trimmed = trimmed.Substring(0, trimmed.Length - valueSuffix.Length);
        }

        if (trimmed.Length == 0)
        {
            return null;
        }

        var segments = trimmed.Split(Constants.PathSeparator);

        // The forward function alternates attribute-name segments (indices 0, 2, 4, …) with
        // "attributes" markers (indices 1, 3, 5, …), so a well-formed path always ends on an
        // attribute name — i.e. an ODD total segment count. An even count means the path
        // terminated on the "attributes" marker (e.g. "attributes.timeRange.attributes"),
        // which the forward function cannot produce. Reject up-front so the loop never
        // returns a partial result.
        if (segments.Length % 2 == 0)
        {
            return null;
        }

        var result = new StringBuilder();
        var currentProvider = provider;

        for (var i = 0; i < segments.Length; i++)
        {
            if (i % 2 == 1)
            {
                // Odd-indexed segments must be the literal "attributes" record-hop marker.
                // Anything else means the path doesn't fit the forward function's output
                // shape (could be a hand-edited index spec or a non-CK field).
                if (!string.Equals(segments[i], Constants.AttributesName, StringComparison.Ordinal))
                {
                    return null;
                }

                continue;
            }

            var camelCaseName = segments[i];
            if (string.IsNullOrEmpty(camelCaseName))
            {
                return null;
            }

            var pascalCaseName = camelCaseName.ToPascalCase();
            if (!currentProvider.TryGetAttribute(pascalCaseName, out var valueType))
            {
                return null;
            }

            if (result.Length > 0)
            {
                result.Append(Constants.PathSeparator);
            }
            result.Append(pascalCaseName);

            // If this segment is a record-typed attribute, the next segments live inside its
            // record graph. We navigate into the record and require the segment AFTER the
            // "attributes" marker to use the new provider.
            if (valueType is AttributeValueTypesDto.Record or AttributeValueTypesDto.RecordArray)
            {
                var recordProvider = currentProvider.NavigateToRecord(pascalCaseName);
                if (recordProvider == null)
                {
                    return null;
                }

                currentProvider = recordProvider;
            }
            else if (i + 1 < segments.Length)
            {
                // A non-record attribute can't have further nested attribute segments. If
                // there's still path left, the path is malformed against our CK type.
                return null;
            }
        }

        return result.ToString();
    }

    /// <summary>
    /// Validates whether a CK attribute path is valid against the given metadata provider.
    /// </summary>
    /// <param name="attributePath">The CK attribute path to validate</param>
    /// <param name="provider">The metadata provider for attribute lookups</param>
    /// <returns>True if the path is valid</returns>
    public static bool IsValidAttributePath(string attributePath, IAttributeMetadataProvider provider)
    {
        var pathTerms = RtPathEvaluator.TokenizePath(attributePath);

        var currentProvider = provider;
        AttributeValueTypesDto lastValueType = default;

        foreach (var pathTerm in pathTerms)
        {
            switch (pathTerm.Type)
            {
                case PathType.Attribute:
                    if (currentProvider.TryGetAttribute(pathTerm.Value.ToPascalCase(), out var valueType))
                    {
                        lastValueType = valueType;

                        if (valueType is AttributeValueTypesDto.Record or AttributeValueTypesDto.RecordArray)
                        {
                            var recordProvider = currentProvider.NavigateToRecord(pathTerm.Value.ToPascalCase());
                            if (recordProvider == null)
                            {
                                return false;
                            }

                            currentProvider = recordProvider;
                        }
                    }
                    else
                    {
                        return false;
                    }

                    break;

                case PathType.ArrayIndex:
                    if (lastValueType is not (AttributeValueTypesDto.StringArray
                        or AttributeValueTypesDto.IntArray
                        or AttributeValueTypesDto.RecordArray))
                    {
                        return false;
                    }

                    break;

                default:
                    return false;
            }
        }

        return true;
    }
}
