using System.Text.Json.Serialization;

namespace EphemeralMongo.Download;

internal sealed class MongoArchiveDto
{
    [JsonPropertyName("url")]
    public string Url { get; set; } = string.Empty;

    [JsonPropertyName("sha256")]
    public string Sha256 { get; set; } = string.Empty;
}