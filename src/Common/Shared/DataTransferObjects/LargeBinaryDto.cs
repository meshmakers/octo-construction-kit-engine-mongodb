using System;
using System.Text.Json.Serialization;

namespace Meshmakers.Octo.Common.Shared.DataTransferObjects;

public class LargeBinaryInfoDto
{
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    [JsonConverter(typeof(OctoObjectIdConverter))]
    public OctoObjectId? BinaryId { get; set; }

    public string? ContentType { get; init; }
    public string? Filename { get; init; }
    public DateTime UploadDateTime { get; init; }
    public long Length { get; init; }
    public Uri? DownloadUri { get; init; }
}
