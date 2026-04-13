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
