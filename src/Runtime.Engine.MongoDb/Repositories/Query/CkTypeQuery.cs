using Meshmakers.Common.Metrics.Context;
using Meshmakers.Octo.ConstructionKit.Contracts;
using Meshmakers.Octo.ConstructionKit.Contracts.DataTransferObjects;
using Meshmakers.Octo.Runtime.Contracts.MongoDb.Repositories.Entities;
using Meshmakers.Octo.Runtime.Engine.MongoDb.Repositories.MongoDb;

using MongoDB.Bson;
using MongoDB.Driver;

namespace Meshmakers.Octo.Runtime.Engine.MongoDb.Repositories.Query;

internal class CkTypeQuery(IMetricsContext metricsContext, IMongoDbRepositoryDataSource mongoDbRepositoryDataSource)
    : SingleOriginCkQuery<CkId<CkTypeId>, CkType>(metricsContext, mongoDbRepositoryDataSource.CkTypes)
{
    private readonly List<FilterDefinition<CkType>> _rtCkIdFilters = new();

    protected override void AddPreFieldFilters(List<FilterDefinition<CkType>> filters)
    {
        filters.Add(Builders<CkType>.Filter.Eq(ckType => ckType.ModelState, ModelState.Available));
        filters.AddRange(_rtCkIdFilters);
        base.AddPreFieldFilters(filters);
    }


    internal void AddRtCkIdFilter<TField>(IReadOnlyList<TField>? ids)
    {
        if (ids == null || !ids.Any())
        {
            return;
        }

        // We need to build regex filters to match the _id field.
        // From "System/Configuration" we create a regex filter "^System-.*/Configuration-.*$"
        // From "System-1/Configuration-2" we create a regex filter "^System-.*/Configuration-2.*$"
        // From "System-2/Configuration-2" we create a regex filter "^System-.*/Configuration-2.*$"
        // The first part (model) always gets a wildcard for version, the last part (type) keeps the version if specified
        // but adds .* suffix to match any patch version.
        // Since MongoDB's $in operator does not support regex patterns directly,
        // we use $or with individual $regex filters.
        var regexFilters = new List<FilterDefinition<CkType>>();
        foreach (var id in ids)
        {
            var idString = id?.ToString();
            if (string.IsNullOrEmpty(idString))
            {
                continue;
            }
            var parts = idString.Split('/');
            for (int i = 0; i < parts.Length; i++)
            {
                var isLastPart = i == parts.Length - 1;
                var dashIndex = parts[i].LastIndexOf('-');

                if (!isLastPart)
                {
                    // For non-last parts (model parts), always use wildcard for version
                    if (dashIndex > 0)
                    {
                        // Has a dash, extract name and replace version with wildcard
                        parts[i] = parts[i].Substring(0, dashIndex) + "-.*";
                    }
                    else
                    {
                        // No dash, add wildcard for version
                        parts[i] += "-.*";
                    }
                }
                else
                {
                    // For the last part (type), keep version if specified but add .* suffix
                    if (dashIndex > 0)
                    {
                        var afterDash = parts[i].Substring(dashIndex + 1);
                        if (int.TryParse(afterDash, out _))
                        {
                            // Numeric version - keep it and add .* suffix for patch versions
                            parts[i] += ".*";
                        }
                        else
                        {
                            // Not a numeric version, replace with wildcard
                            parts[i] = parts[i].Substring(0, dashIndex) + "-.*";
                        }
                    }
                    else
                    {
                        // No version suffix found, add wildcard
                        parts[i] += "-.*";
                    }
                }
            }
            var regexPattern = "^" + string.Join("/", parts) + "$";
            regexFilters.Add(Builders<CkType>.Filter.Regex(Constants.IdField, new BsonRegularExpression(regexPattern)));
        }

        if (regexFilters.Count > 0)
        {
            _rtCkIdFilters.Add(Builders<CkType>.Filter.Or(regexFilters));
        }
    }
}
