using System.Text.Json.Serialization;

namespace EphemeralMongo.Download;

internal sealed class MongoVersionDto
{
    [JsonPropertyName("production_release")]
    public bool ProductionRelease { get; set; }

    [JsonPropertyName("version")]
    public string Version { get; set; } = string.Empty;

    [JsonPropertyName("downloads")]
    public MongoDownloadDto[] Downloads { get; set; } = [];
}