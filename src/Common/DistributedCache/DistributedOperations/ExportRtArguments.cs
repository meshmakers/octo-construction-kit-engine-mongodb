using System.Text.Json.Serialization;
using Meshmakers.Octo.ConstructionKit.Contracts;
using Meshmakers.Octo.ConstructionKit.Contracts.Serialization;

namespace Meshmakers.Octo.Common.DistributedCache.DistributedOperations;

/// <summary>
/// Export runtime data arguments
/// </summary>
public record ExportRtArguments : ArgumentBase
{
    /// <summary>
    /// Constructor
    /// </summary>
    /// <param name="tenantId"></param>
    /// <param name="queryId"></param>
    public ExportRtArguments(string tenantId, OctoObjectId queryId) 
        : base(tenantId)
    {
        QueryId = queryId;
    }
    
    /// <summary>
    /// Query id to export
    /// </summary>
    [JsonConverter(typeof(OctoObjectIdConverter))]
    public OctoObjectId QueryId { get; set; }
}