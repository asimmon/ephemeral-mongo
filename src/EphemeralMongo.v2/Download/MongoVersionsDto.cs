using System.Text.Json.Serialization;

namespace EphemeralMongo.Download;

internal sealed class MongoVersionsDto
{
    [JsonPropertyName("versions")]
    public MongoVersionDto[] Versions { get; set; } = [];
}