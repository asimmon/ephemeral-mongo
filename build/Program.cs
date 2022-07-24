#pragma warning disable CA1050
#pragma warning disable SA1649

using System;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
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
public sealed class DownloadMongoTask : FrostingTask<BuildContext>
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

    public override void Run(BuildContext context)
    {
        // Parse JSON file containing all versions and OS-specific download URLs
        const string currentJsonUrl = "https://s3.amazonaws.com/downloads.mongodb.org/current.json";
        var jsonFilePath = context.DownloadFile(currentJsonUrl);

        MongoVersionsDto currentJson;

        try
        {
            var currentJsonRaw = File.ReadAllText(jsonFilePath.FullPath);
            currentJson = JsonSerializer.Deserialize<MongoVersionsDto>(currentJsonRaw) ?? throw new Exception("An error occured while parsing " + currentJsonUrl);
        }
        finally
        {
            File.Delete(jsonFilePath.FullPath);
        }

        foreach (var project in Projects)
        {
            // Find and download mongoDB for the current project
            var version = currentJson.Versions.First(x => x.ProductionRelease && x.Version.StartsWith(project.Version));
            var download = version.Downloads.Single(x => x.Architecture == project.Architecture && x.Edition == project.Edition && x.Target == project.Target);

            context.Log.Information("Downloading {0}", download.Archive.Url);
            var archiveFilePath = context.DownloadFile(download.Archive.Url);
            context.Log.Information("Archive downloaded to {0}", archiveFilePath.FullPath);

            try
            {
                // Verify SHA256 hash
                context.Log.Information("Verifying SHA256 hash...");
                using (var archiveFileHasher = SHA256.Create())
                using (var archiveFileStream = File.OpenRead(archiveFilePath.FullPath))
                {
                    var hashBytes = archiveFileHasher.ComputeHash(archiveFileStream);
                    var hashStr = Convert.ToHexString(hashBytes);

                    if (!hashStr.Equals(download.Archive.Sha256, StringComparison.OrdinalIgnoreCase))
                    {
                        throw new InvalidOperationException("An error occured during download, hashes don't match for " + download.Archive.Url);
                    }
                }

                // Uncompress archive
                var uncompressDir = DirectoryPath.FromString(Path.Combine(Constants.RuntimesToolsPath, project.Name));
                context.Log.Information("Uncompressing to {0}", uncompressDir.FullPath);

                if (download.Archive.Url.EndsWith(".zip"))
                {
                    context.ZipUncompress(archiveFilePath, uncompressDir);
                }
                else if (download.Archive.Url.EndsWith(".tgz"))
                {
                    context.GZipUncompress(archiveFilePath, uncompressDir);
                }
                else
                {
                    throw new InvalidOperationException("Unexpected file format for " + download.Archive.Url);
                }

                // Find mongod executable
                var mongoExecutable = context.Globber.GetFiles(Path.Combine(uncompressDir.FullPath, "*", "bin", project.MongoExecutableFileName)).FirstOrDefault();
                if (mongoExecutable == null)
                {
                    throw new InvalidOperationException("Could not find mongod executable in " + uncompressDir.FullPath);
                }

                var newMongoExecutablePath = Path.Combine(uncompressDir.FullPath, project.MongoExecutableFileName);

                // Find miscellaneous files such as LICENSE, README, THIRD-PARTY-NOTICES, ...
                var miscFiles = context.Globber.GetFiles(Path.Combine(uncompressDir.FullPath, "*", "*")).ToList();
                if (!miscFiles.Any(x => x.FullPath.Contains("README")))
                {
                    throw new InvalidOperationException("Could not find README and license files");
                }

                // Copy mongod executable with its associated license and README file together to the upper directory, delete any remaining file and directory
                context.Log.Information("Moving {0} to {1}", mongoExecutable.FullPath, newMongoExecutablePath);
                File.Move(mongoExecutable.FullPath, newMongoExecutablePath);

                foreach (var miscFile in miscFiles)
                {
                    var newMiscFilePath = Path.Combine(uncompressDir.FullPath, miscFile.GetFilename().ToString());
                    context.Log.Information("Moving {0} to {1}", miscFile.FullPath, newMiscFilePath);
                    File.Move(miscFile.FullPath, newMiscFilePath);
                }

                Directory.Delete(mongoExecutable.GetDirectory().GetParent().FullPath, recursive: true);
            }
            finally
            {
                File.Delete(archiveFilePath.FullPath);
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
[IsDependentOn(typeof(PackTask))]
public sealed class DefaultTask : FrostingTask
{
}