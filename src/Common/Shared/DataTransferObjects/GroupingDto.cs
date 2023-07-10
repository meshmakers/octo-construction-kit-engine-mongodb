using System.Collections.Generic;
using Newtonsoft.Json;

namespace Meshmakers.Octo.Common.Shared.DataTransferObjects;

public class GroupingDto
{
    [JsonProperty(NullValueHandling = NullValueHandling.Ignore)]
    public IEnumerable<string>? GroupByAttributeNames { get; set; }

    [JsonProperty(NullValueHandling = NullValueHandling.Ignore)]
    public IEnumerable<object?>? Keys { get; set; } = null!;

    [JsonProperty(NullValueHandling = NullValueHandling.Ignore)]
    public long? Count { get; set; }

    [JsonProperty(NullValueHandling = NullValueHandling.Ignore)]
    public IEnumerable<StatisticsDto>? CountStatistics { get; set; }

    [JsonProperty(NullValueHandling = NullValueHandling.Ignore)]
    public IEnumerable<StatisticsDto>? MinStatistics { get; set; }

    [JsonProperty(NullValueHandling = NullValueHandling.Ignore)]
    public IEnumerable<StatisticsDto>? MaxStatistics { get; set; }

    [JsonProperty(NullValueHandling = NullValueHandling.Ignore)]
    public IEnumerable<StatisticsDto>? AvgStatistics { get; set; }
}