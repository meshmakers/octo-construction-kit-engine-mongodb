using Meshmakers.Octo.Runtime.Contracts.StreamData;
using Meshmakers.Octo.ConstructionKit.Contracts;
using Meshmakers.Octo.Runtime.Contracts.RepositoryEntities;

namespace Meshmakers.Octo.Runtime.Engine.CrateDb.Dtos;

/// <summary>
/// Represents a data point.
/// </summary>
public class DataPointDto : RtTypeWithAttributes
{
    /// <summary>
    /// 
    /// </summary>
    /// <param name="attributes"></param>
    public DataPointDto(Dictionary<string, object?> attributes) : base(attributes)
    {
        
    }
    
    /// <summary>
    /// The id of the entity that the datapoint is associated with.
    /// </summary>
    public OctoObjectId? RtId { get; set; }
    
    /// <summary>
    /// The type id of the entity that the datapoint is associated with.
    /// </summary>
    public RtCkId<CkTypeId>? CkTypeId { get; set; }
    
    /// <summary>
    /// the optional well known name of the entity that the datapoint is associated with.
    /// </summary>
    public string? RtWellKnownName { get; set; }
    
    /// <summary>
    /// Whether the row was entered manually or delivered automatically (AB#5356). Written to the
    /// <c>row_origin</c> column of raw and time-range tables; ignored for rollup tables.
    /// </summary>
    public ArchiveRowOrigin Origin { get; set; } = ArchiveRowOrigin.Automated;

    /// <summary>
    /// The timestamp in UTC.
    /// </summary>
    public DateTime Timestamp { get; set; }
    
    /// <summary>
    /// The creation date time of the entity
    /// </summary>
    public DateTime RtCreationDateTime { get; set; }
    
    /// <summary>
    /// The last changed date time of the entity
    /// </summary>
    public DateTime RtChangedDateTime { get; set; }
    
    /// <summary>
    /// 
    /// </summary>
    /// <returns></returns>
    protected override string GetLocation()
    {
        return "StreamData";
    }
}