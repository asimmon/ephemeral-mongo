using System;
using System.Text.Json.Serialization;

namespace Build;

public sealed class ProjectInfo
{
    public ProjectInfo(string name, string target, string architecture, string edition, string version, string rid)
    {
        this.Name = name;
        this.Target = target;
        this.Architecture = architecture;
        this.Edition = edition;
        this.Version = version;
        this.Rid = rid;
    }

    public string Name { get; }

    public string Target { get; }

    public string Architecture { get; }

    public string Edition { get; }

    public string Version { get; }

    public string Rid { get; }

    public string MongoExecutableFileName => this.Target.Contains("windows", StringComparison.OrdinalIgnoreCase) ? "mongod.exe" : "mongod";

    public string MongoImportExecutableFileName => this.Target.Contains("windows", StringComparison.OrdinalIgnoreCase) ? "mongoimport.exe" : "mongoimport";

    public string MongoExportExecutableFileName => this.Target.Contains("windows", StringComparison.OrdinalIgnoreCase) ? "mongoexport.exe" : "mongoexport";
}

public sealed class ToolsVersionsDto
{
    [JsonPropertyName("versions")]
    public ToolsVersionDto[] Versions { get; set; } = Array.Empty<ToolsVersionDto>();
}

public sealed class ToolsVersionDto
{
    [JsonPropertyName("downloads")]
    public ToolsDownloadDto[] Downloads { get; set; } = Array.Empty<ToolsDownloadDto>();
}

public sealed class ToolsDownloadDto
{
    [JsonPropertyName("name")]
    public string Name { get; set; } = string.Empty;

    [JsonPropertyName("arch")]
    public string Architecture { get; set; } = string.Empty;

    [JsonPropertyName("archive")]
    public ToolsArchiveDto Archive { get; set; } = ToolsArchiveDto.Empty;
}

public sealed class ToolsArchiveDto
{
    public static readonly ToolsArchiveDto Empty = new ToolsArchiveDto();

    [JsonPropertyName("url")]
    public string Url { get; set; } = string.Empty;

    [JsonPropertyName("sha256")]
    public string Sha256 { get; set; } = string.Empty;
}

public sealed class MongoVersionsDto
{
    [JsonPropertyName("versions")]
    public MongoVersionDto[] Versions { get; set; } = Array.Empty<MongoVersionDto>();
}

public sealed class MongoVersionDto
{
    [JsonPropertyName("production_release")]
    public bool ProductionRelease { get; set; }

    [JsonPropertyName("version")]
    public string Version { get; set; } = string.Empty;

    [JsonPropertyName("downloads")]
    public MongoDownloadDto[] Downloads { get; set; } = Array.Empty<MongoDownloadDto>();
}

public sealed class MongoDownloadDto
{
    [JsonPropertyName("arch")]
    public string Architecture { get; set; } = string.Empty;

    [JsonPropertyName("edition")]
    public string Edition { get; set; } = string.Empty;

    [JsonPropertyName("target")]
    public string Target { get; set; } = string.Empty;

    [JsonPropertyName("archive")]
    public MongoArchiveDto Archive { get; set; } = MongoArchiveDto.Empty;
}

public sealed class MongoArchiveDto
{
    public static readonly MongoArchiveDto Empty = new MongoArchiveDto();

    [JsonPropertyName("url")]
    public string Url { get; set; } = string.Empty;

    [JsonPropertyName("sha256")]
    public string Sha256 { get; set; } = string.Empty;
}