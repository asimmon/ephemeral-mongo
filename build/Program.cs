#pragma warning disable CA1050
#pragma warning disable SA1649

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading.Tasks;
using Cake.Common;
using Cake.Common.IO;
using Cake.Common.Net;
using Cake.Common.Tools.DotNet;
using Cake.Common.Tools.DotNet.MSBuild;
using Cake.Common.Tools.DotNet.NuGet.Push;
using Cake.Common.Tools.DotNet.Pack;
using Cake.Common.Tools.GitVersion;
using Cake.Compression;
using Cake.Core;
using Cake.Core.Diagnostics;
using Cake.Core.IO;
using Cake.Frosting;
using Path = System.IO.Path;
using File = System.IO.File;

return new CakeHost()
    .InstallTool(new Uri("dotnet:?package=GitVersion.Tool&version=5.10.1"))
    .UseContext<BuildContext>()
    .Run(args);

public static class Constants
{
    public const string Release = "Release";

    public static readonly string SourceDirectoryPath = Path.Combine("..", "src");
    public static readonly string OutputDirectoryPath = Path.Combine("..", ".output");
    public static readonly string SolutionPath = Path.Combine(SourceDirectoryPath, "Askaiser.EphemeralMongo.sln");
    public static readonly string RuntimesToolsPath = Path.Combine(SourceDirectoryPath, "Askaiser.EphemeralMongo.Runtimes", "tools");
}

public class BuildContext : FrostingContext
{
    public BuildContext(ICakeContext context) : base(context)
    {
        this.NugetApiKey = context.Argument("nuget-api-key", string.Empty);
        this.NugetSource = context.Argument("nuget-source", string.Empty);
    }

    public DotNetMSBuildSettings MSBuildSettings { get; } = new DotNetMSBuildSettings();

    public string NugetApiKey { get; }

    public string NugetSource { get; }

    public void AddMSBuildSetting(string name, string value, bool log = false)
    {
        if (log)
        {
            this.Log.Information(name + ": " + value);
        }

        if (!string.IsNullOrWhiteSpace(value))
        {
            this.MSBuildSettings.Properties[name] = new[] { value };
        }
    }
}

[TaskName("Clean")]
public sealed class CleanTask : FrostingTask<BuildContext>
{
    public override void Run(BuildContext context)
    {
        var objGlobPath = Path.Combine(Constants.SourceDirectoryPath, "*", "obj");
        var binGlobPath = Path.Combine(Constants.SourceDirectoryPath, "*", "bin");

        context.CleanDirectories(Constants.OutputDirectoryPath);
        context.CleanDirectories(Constants.RuntimesToolsPath);
        context.CleanDirectories(objGlobPath);
        context.CleanDirectories(binGlobPath);
    }
}

[TaskName("GitVersion")]
public sealed class GitVersionTask : FrostingTask<BuildContext>
{
    public override void Run(BuildContext context)
    {
        var gitVersion = context.GitVersion();

        context.AddMSBuildSetting("Version", gitVersion.NuGetVersion, log: true);
        context.AddMSBuildSetting("VersionPrefix", gitVersion.MajorMinorPatch, log: true);
        context.AddMSBuildSetting("VersionSuffix", gitVersion.PreReleaseTag, log: true);
        context.AddMSBuildSetting("PackageVersion", gitVersion.FullSemVer, log: true);
        context.AddMSBuildSetting("InformationalVersion", gitVersion.InformationalVersion, log: true);
        context.AddMSBuildSetting("AssemblyVersion", gitVersion.AssemblySemVer, log: true);
        context.AddMSBuildSetting("FileVersion", gitVersion.AssemblySemFileVer, log: true);
        context.AddMSBuildSetting("RepositoryBranch", gitVersion.BranchName, log: true);
        context.AddMSBuildSetting("RepositoryCommit", gitVersion.Sha, log: true);
    }
}

[TaskName("DownloadMongo")]
[IsDependentOn(typeof(CleanTask))]
[IsDependentOn(typeof(GitVersionTask))]
public sealed class DownloadMongoTask : AsyncFrostingTask<BuildContext>
{
    private static readonly ProjectInfo[] Projects =
    {
        // MongoDB 6.x
        new ProjectInfo("Askaiser.EphemeralMongo6.windows-x86-x64", "windows", "x86_64", "base", "6"),
        new ProjectInfo("Askaiser.EphemeralMongo6.macos-x86-x64", "macos", "x86_64", "base", "6"),
        new ProjectInfo("Askaiser.EphemeralMongo6.ubuntu1804-x86-x64", "ubuntu1804", "x86_64", "targeted", "6"),

        // MongoDB 5.x
        new ProjectInfo("Askaiser.EphemeralMongo5.windows-x86-x64", "windows", "x86_64", "base", "5"),
        new ProjectInfo("Askaiser.EphemeralMongo5.macos-x86-x64", "macos", "x86_64", "base", "5"),
        new ProjectInfo("Askaiser.EphemeralMongo5.ubuntu1804-x86-x64", "ubuntu1804", "x86_64", "targeted", "5"),

        // MongoDB 4.x
        new ProjectInfo("Askaiser.EphemeralMongo4.windows-x86-x64", "windows", "x86_64", "base", "4"),
        new ProjectInfo("Askaiser.EphemeralMongo4.macos-x86-x64", "macos", "x86_64", "base", "4"),
        new ProjectInfo("Askaiser.EphemeralMongo4.ubuntu1804-x86-x64", "ubuntu1804", "x86_64", "targeted", "4"),
    };

    public override async Task RunAsync(BuildContext context)
    {
        var tasks = new List<Task>();

        foreach (var project in Projects)
        {
            tasks.Add(Task.Run(() => RunProject(context, project)));
        }

        await Task.WhenAll(tasks);
    }

    private static void RunProject(BuildContext context, ProjectInfo project)
    {
        var mongoVersions = GetMongoVersions(context);
        var toolsVersions = GetToolsVersions(context);

        // Find MongoDB for the current project
        var mongoVersion = mongoVersions.Versions.FirstOrDefault(x => x.ProductionRelease && x.Version.StartsWith(project.Version))
            ?? throw new InvalidOperationException("Could not find production release for MongoDB " + project.Version);

        var mongoDownload = mongoVersion.Downloads.SingleOrDefault(x => x.Architecture == project.Architecture && x.Edition == project.Edition && x.Target == project.Target)
            ?? throw new InvalidOperationException("Could not find MongoDB " + project.Architecture + ", " + project.Edition + ", " + project.Target);

        // Find MongoDB tools for the current project
        var toolsVersion = toolsVersions.Versions.FirstOrDefault()
            ?? throw new InvalidOperationException("Could not find MongoDB tools");

        var toolsDownload = toolsVersion.Downloads.SingleOrDefault(x => x.Architecture == project.Architecture && x.Name == project.Target)
            ?? throw new InvalidOperationException("Could not find MongoDB tools " + project.Architecture + ", " + project.Target);

        // Download MongoDB
        context.Log.Information("Downloading MongoDB {0}", mongoDownload.Archive.Url);
        var mongoArchiveFilePath = context.DownloadFile(mongoDownload.Archive.Url);
        using var tmp1 = new DisposableFile(mongoArchiveFilePath.FullPath);
        context.Log.Information("MongoDB downloaded to {0}", mongoArchiveFilePath.FullPath);

        // Download MongoDB tools
        context.Log.Information("Downloading MongoDB tools {0}", toolsDownload.Archive.Url);
        var toolsArchiveFilePath = context.DownloadFile(toolsDownload.Archive.Url);
        using var tmp2 = new DisposableFile(toolsArchiveFilePath.FullPath);
        context.Log.Information("MongoDB tools downloaded to {0}", toolsArchiveFilePath.FullPath);

        // Verify SHA256 hash
        EnsureHashMatch(context, mongoArchiveFilePath.FullPath, mongoDownload.Archive.Sha256);
        EnsureHashMatch(context, toolsArchiveFilePath.FullPath, toolsDownload.Archive.Sha256);

        // Uncompress archive
        var projectDir = Directory.CreateDirectory(Path.Combine(Constants.RuntimesToolsPath, project.Name));
        var binDir = Directory.CreateDirectory(Path.Combine(projectDir.FullName, "bin"));
        var mongoUncompressDir = Directory.CreateDirectory(Path.Combine(projectDir.FullName, "community-server"));
        var toolsUncompressDir = Directory.CreateDirectory(Path.Combine(projectDir.FullName, "database-tools"));

        Uncompress(context, mongoDownload.Archive.Url, mongoArchiveFilePath.FullPath, mongoUncompressDir.FullName);
        Uncompress(context, toolsDownload.Archive.Url, toolsArchiveFilePath.FullPath, toolsUncompressDir.FullName);

        // Find mongod executable
        var mongoExecutable = context.Globber.GetFiles(Path.Combine(mongoUncompressDir.FullName, "*", "bin", project.MongoExecutableFileName)).FirstOrDefault()
            ?? throw new InvalidOperationException("Could not find MongoDB executable in " + mongoUncompressDir.FullName);

        // Find mongoimport
        var mongoImportExecutable = context.Globber.GetFiles(Path.Combine(toolsUncompressDir.FullName, "*", "bin", project.MongoImportExecutableFileName)).FirstOrDefault()
            ?? throw new InvalidOperationException("Could not find MongoDB import tool executable in " + toolsUncompressDir.FullName);

        // Find mongoexport
        var mongoExportExecutable = context.Globber.GetFiles(Path.Combine(toolsUncompressDir.FullName, "*", "bin", project.MongoExportExecutableFileName)).FirstOrDefault()
            ?? throw new InvalidOperationException("Could not find MongoDB export tool executable in " + toolsUncompressDir.FullName);

        // Prepare their new paths
        var newMongoExecutablePath = Path.Combine(binDir.FullName, project.MongoExecutableFileName);
        var newMongoImportExecutablePath = Path.Combine(binDir.FullName, project.MongoImportExecutableFileName);
        var newMongoExportExecutablePath = Path.Combine(binDir.FullName, project.MongoExportExecutableFileName);

        // Find miscellaneous files such as LICENSE, README, THIRD-PARTY-NOTICES, ...
        var mongoMiscFiles = context.Globber.GetFiles(Path.Combine(mongoUncompressDir.FullName, "*", "*")).ToList();
        var toolsMiscFiles = context.Globber.GetFiles(Path.Combine(toolsUncompressDir.FullName, "*", "*")).ToList();

        // Copy mongod executable with its associated license and README file together to the upper directory, delete any remaining file and directory
        context.Log.Information("Moving MongoDB {0} to {1}", mongoExecutable.FullPath, newMongoExecutablePath);
        File.Move(mongoExecutable.FullPath, newMongoExecutablePath);

        context.Log.Information("Moving MongoDB import {0} to {1}", mongoImportExecutable.FullPath, newMongoImportExecutablePath);
        File.Move(mongoImportExecutable.FullPath, newMongoImportExecutablePath);

        context.Log.Information("Moving MongoDB export {0} to {1}", mongoExportExecutable.FullPath, newMongoExportExecutablePath);
        File.Move(mongoExportExecutable.FullPath, newMongoExportExecutablePath);

        foreach (var mongoMiscFile in mongoMiscFiles)
        {
            var newMongoMiscFilePath = Path.Combine(mongoUncompressDir.FullName, mongoMiscFile.GetFilename().ToString());
            context.Log.Information("Moving misc file {0} to {1}", mongoMiscFile.FullPath, newMongoMiscFilePath);
            File.Move(mongoMiscFile.FullPath, newMongoMiscFilePath);
        }

        foreach (var toolsMiscFile in toolsMiscFiles)
        {
            var newToolsMiscFilePath = Path.Combine(toolsUncompressDir.FullName, toolsMiscFile.GetFilename().ToString());
            context.Log.Information("Moving misc file {0} to {1}", toolsMiscFile.FullPath, newToolsMiscFilePath);
            File.Move(toolsMiscFile.FullPath, newToolsMiscFilePath);
        }

        Directory.Delete(mongoExecutable.GetDirectory().GetParent().FullPath, recursive: true);
        Directory.Delete(mongoImportExecutable.GetDirectory().GetParent().FullPath, recursive: true);
    }

    private static MongoVersionsDto GetMongoVersions(BuildContext context)
    {
        // Parse JSON file containing all versions and OS-specific download URLs
        const string currentJsonUrl = "https://s3.amazonaws.com/downloads.mongodb.org/current.json";
        var jsonFilePath = context.DownloadFile(currentJsonUrl);

        try
        {
            var currentJsonRaw = File.ReadAllText(jsonFilePath.FullPath);
            return JsonSerializer.Deserialize<MongoVersionsDto>(currentJsonRaw) ?? throw new Exception("An error occured while parsing " + currentJsonUrl);
        }
        finally
        {
            File.Delete(jsonFilePath.FullPath);
        }
    }

    private static ToolsVersionsDto GetToolsVersions(BuildContext context)
    {
        // Parse JSON file containing all versions and OS-specific download URLs
        const string currentJsonUrl = "https://s3.amazonaws.com/downloads.mongodb.org/tools/db/release.json";
        var jsonFilePath = context.DownloadFile(currentJsonUrl);

        try
        {
            var currentJsonRaw = File.ReadAllText(jsonFilePath.FullPath);
            return JsonSerializer.Deserialize<ToolsVersionsDto>(currentJsonRaw) ?? throw new Exception("An error occured while parsing " + currentJsonUrl);
        }
        finally
        {
            File.Delete(jsonFilePath.FullPath);
        }
    }

    private static void EnsureHashMatch(BuildContext context, string filePath, string expectedHexHash)
    {
        // Verify SHA256 hash
        context.Log.Information("Verifying SHA256 hash of " + filePath + "...");
        using (var archiveFileHasher = SHA256.Create())
        using (var archiveFileStream = File.OpenRead(filePath))
        {
            var hashBytes = archiveFileHasher.ComputeHash(archiveFileStream);
            var hashStr = Convert.ToHexString(hashBytes);

            if (!hashStr.Equals(expectedHexHash, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException("An error occured during download, hashes don't match for " + filePath);
            }
        }
    }

    private static void Uncompress(BuildContext context, string archiveUrl, string archiveFilePath, string uncompressDir)
    {
        context.Log.Information("Uncompressing to {0}", uncompressDir);

        if (archiveUrl.EndsWith(".zip"))
        {
            context.ZipUncompress(archiveFilePath, uncompressDir);
        }
        else if (archiveUrl.EndsWith(".tgz"))
        {
            context.GZipUncompress(archiveFilePath, uncompressDir);
        }
        else
        {
            throw new InvalidOperationException("Unexpected file format for " + archiveUrl);
        }
    }

    private sealed class DisposableFile : IDisposable
    {
        private readonly string _filePath;

        public DisposableFile(string filePath)
        {
            this._filePath = filePath;
        }

        public void Dispose()
        {
            try
            {
                File.Delete(this._filePath);
            }
            catch
            {
                // ignored
            }
        }
    }

    private sealed class ProjectInfo
    {
        public ProjectInfo(string name, string target, string architecture, string edition, string version)
        {
            this.Name = name;
            this.Target = target;
            this.Architecture = architecture;
            this.Edition = edition;
            this.Version = version;
        }

        public string Name { get; }

        public string Target { get;  }

        public string Architecture { get; }

        public string Edition { get; }

        public string Version { get; }

        public string MongoExecutableFileName => this.Target.Contains("windows", StringComparison.OrdinalIgnoreCase) ? "mongod.exe" : "mongod";

        public string MongoImportExecutableFileName => this.Target.Contains("windows", StringComparison.OrdinalIgnoreCase) ? "mongoimport.exe" : "mongoimport";

        public string MongoExportExecutableFileName => this.Target.Contains("windows", StringComparison.OrdinalIgnoreCase) ? "mongoexport.exe" : "mongoexport";
    }

    private sealed class ToolsVersionsDto
    {
        [JsonPropertyName("versions")]
        public ToolsVersionDto[] Versions { get; set; } = Array.Empty<ToolsVersionDto>();
    }

    private sealed class ToolsVersionDto
    {
        [JsonPropertyName("downloads")]
        public ToolsDownloadDto[] Downloads { get; set; } = Array.Empty<ToolsDownloadDto>();
    }

    private sealed class ToolsDownloadDto
    {
        [JsonPropertyName("name")]
        public string Name { get; set; } = string.Empty;

        [JsonPropertyName("arch")]
        public string Architecture { get; set; } = string.Empty;

        [JsonPropertyName("archive")]
        public ToolsArchiveDto Archive { get; set; } = ToolsArchiveDto.Empty;
    }

    private sealed class ToolsArchiveDto
    {
        public static readonly ToolsArchiveDto Empty = new ToolsArchiveDto();

        [JsonPropertyName("url")]
        public string Url { get; set; } = string.Empty;

        [JsonPropertyName("sha256")]
        public string Sha256 { get; set; } = string.Empty;
    }

    private sealed class MongoVersionsDto
    {
        [JsonPropertyName("versions")]
        public MongoVersionDto[] Versions { get; set; } = Array.Empty<MongoVersionDto>();
    }

    private sealed class MongoVersionDto
    {
        [JsonPropertyName("production_release")]
        public bool ProductionRelease { get; set; }

        [JsonPropertyName("version")]
        public string Version { get; set; } = string.Empty;

        [JsonPropertyName("downloads")]
        public MongoDownloadDto[] Downloads { get; set; } = Array.Empty<MongoDownloadDto>();
    }

    private sealed class MongoDownloadDto
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

    private sealed class MongoArchiveDto
    {
        public static readonly MongoArchiveDto Empty = new MongoArchiveDto();

        [JsonPropertyName("url")]
        public string Url { get; set; } = string.Empty;

        [JsonPropertyName("sha256")]
        public string Sha256 { get; set; } = string.Empty;
    }
}

[TaskName("Pack")]
[IsDependentOn(typeof(DownloadMongoTask))]
public sealed class PackTask : FrostingTask<BuildContext>
{
    public override void Run(BuildContext context) => context.DotNetPack(Constants.SolutionPath, new DotNetPackSettings
    {
        Configuration = Constants.Release,
        MSBuildSettings = context.MSBuildSettings,
        OutputDirectory = Constants.OutputDirectoryPath,
        NoBuild = false,
        NoRestore = false,
        NoLogo = true,
    });
}

[TaskName("Push")]
[IsDependentOn(typeof(PackTask))]
public sealed class PushTask : FrostingTask<BuildContext>
{
    public override void Run(BuildContext context)
    {
        foreach (var packageFilePath in context.GetFiles(Path.Combine(Constants.OutputDirectoryPath, "*.nupkg")))
        {
            context.DotNetNuGetPush(packageFilePath, new DotNetNuGetPushSettings
            {
                ApiKey = context.NugetApiKey,
                Source = context.NugetSource,
                IgnoreSymbols = false
            });
        }
    }
}

[TaskName("Default")]
[IsDependentOn(typeof(DownloadMongoTask))]
public sealed class DefaultTask : FrostingTask
{
}