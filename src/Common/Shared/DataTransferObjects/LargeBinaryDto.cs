using System;
using Newtonsoft.Json;

namespace Meshmakers.Octo.Common.Shared.DataTransferObjects;

public class LargeBinaryInfoDto
{
    [JsonProperty(NullValueHandling = NullValueHandling.Ignore)]
    [JsonConverter(typeof(NewtonOctoObjectIdConverter))]
    public OctoObjectId? BinaryId { get; set; }

    public string? ContentType { get; set; }
    public string? Filename { get; set; }
    public DateTime UploadDateTime { get; set; }
    public long Length { get; set; }
    public Uri? DownloadUri { get; set; }
}
