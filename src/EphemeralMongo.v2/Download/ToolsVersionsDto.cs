using System.Text.Json.Serialization;

namespace EphemeralMongo.Download;

internal sealed class ToolsVersionsDto
{
    [JsonPropertyName("versions")]
    public ToolsVersionDto[] Versions { get; set; } = [];
}