using System.Text.Json.Serialization;

namespace EphemeralMongo.Download;

internal sealed class ToolsVersionDto
{
    [JsonPropertyName("version")]
    public string Version { get; set; } = string.Empty;

    [JsonPropertyName("downloads")]
    public ToolsDownloadDto[] Downloads { get; set; } = [];
}