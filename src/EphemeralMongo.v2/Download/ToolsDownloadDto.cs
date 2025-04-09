using System.Text.Json.Serialization;

namespace EphemeralMongo.Download;

internal sealed class ToolsDownloadDto
{
    [JsonPropertyName("name")]
    public string Name { get; set; } = string.Empty;

    [JsonPropertyName("arch")]
    public string Architecture { get; set; } = string.Empty;

    [JsonPropertyName("archive")]
    public ToolsArchiveDto Archive { get; set; } = new ToolsArchiveDto();
}