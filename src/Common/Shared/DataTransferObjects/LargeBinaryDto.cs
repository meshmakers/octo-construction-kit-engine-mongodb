using System;
using Newtonsoft.Json;

namespace Meshmakers.Octo.Common.Shared.DataTransferObjects;

public class LargeBinaryInfoDto
{
    [JsonProperty(NullValueHandling = NullValueHandling.Ignore)]
    [JsonConverter(typeof(NewtonOctoObjectIdConverter))]
    public OctoObjectId? BinaryId { get; set; }

    public string? ContentType { get; init; }
    public string? Filename { get; init; }
    public DateTime UploadDateTime { get; init; }
    public long Length { get; init; }
    public Uri? DownloadUri { get; init; }
}
