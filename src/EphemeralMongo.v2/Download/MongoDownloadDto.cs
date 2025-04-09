using System.Text.Json.Serialization;

namespace EphemeralMongo.Download;

internal sealed class MongoDownloadDto
{
    [JsonPropertyName("arch")]
    public string Architecture { get; set; } = string.Empty;

    [JsonPropertyName("edition")]
    public string Edition { get; set; } = string.Empty;

    [JsonPropertyName("target")]
    public string Target { get; set; } = string.Empty;

    [JsonPropertyName("archive")]
    public MongoArchiveDto Archive { get; set; } = new MongoArchiveDto();
}