#pragma warning disable CA1050
#pragma warning disable SA1649

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text.Json;
using System.Threading.Tasks;
using Build;
using Cake.Common;
using Cake.Common.IO;
using Cake.Common.Net;
using Cake.Common.Tools.DotNet;
using Cake.Common.Tools.DotNet.Build;
using Cake.Common.Tools.DotNet.MSBuild;
using Cake.Common.Tools.DotNet.NuGet.Push;
using Cake.Common.Tools.DotNet.NuGet.Source;
using Cake.Common.Tools.DotNet.Pack;
using Cake.Common.Tools.DotNet.Restore;
using Cake.Common.Tools.DotNet.Test;
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
    public static readonly string RuntimesPath = Path.Combine(SourceDirectoryPath, "Askaiser.EphemeralMongo.Runtimes", "runtimes");
    public static readonly string TestProjectPath = Path.Combine(SourceDirectoryPath, "Askaiser.EphemeralMongo.Core.Tests", "Askaiser.EphemeralMongo.Core.Tests.csproj");
    public static readonly string CoreProjectPath = Path.Combine(SourceDirectoryPath, "Askaiser.EphemeralMongo.Core", "Askaiser.EphemeralMongo.Core.csproj");
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

    public string PackageVersion { get; set; } = string.Empty;

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
        context.CleanDirectories(Constants.RuntimesPath);
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

        context.PackageVersion = gitVersion.FullSemVer;
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
        new ProjectInfo("Askaiser.EphemeralMongo6.runtime.win-x64", "windows", "x86_64", "base", "6", "win-x64"),
        new ProjectInfo("Askaiser.EphemeralMongo6.runtime.osx-x64", "macos", "x86_64", "base", "6", "osx-x64"),
        new ProjectInfo("Askaiser.EphemeralMongo6.runtime.linux-x64", "ubuntu1804", "x86_64", "targeted", "6", "linux-x64"),

        // MongoDB 5.x
        new ProjectInfo("Askaiser.EphemeralMongo5.runtime.win-x64", "windows", "x86_64", "base", "5", "win-x64"),
        new ProjectInfo("Askaiser.EphemeralMongo5.runtime.osx-x64", "macos", "x86_64", "base", "5", "osx-x64"),
        new ProjectInfo("Askaiser.EphemeralMongo5.runtime.linux-x64", "ubuntu1804", "x86_64", "targeted", "5", "linux-x64"),

        // MongoDB 4.x
        new ProjectInfo("Askaiser.EphemeralMongo4.runtime.win-x64", "windows", "x86_64", "base", "4", "win-x64"),
        new ProjectInfo("Askaiser.EphemeralMongo4.runtime.osx-x64", "macos", "x86_64", "base", "4", "osx-x64"),
        new ProjectInfo("Askaiser.EphemeralMongo4.runtime.linux-x64", "ubuntu1804", "x86_64", "targeted", "4", "linux-x64"),
    };

    public override async Task RunAsync(BuildContext context)
    {
        var tasks = new List<Task>();

        foreach (var project in Projects)
        {
            tasks.Add(Task.Factory.StartNew(() => RunProject(context, project), TaskCreationOptions.LongRunning).Unwrap());
        }

        await Task.WhenAll(tasks);
    }

    private static async Task RunProject(BuildContext context, ProjectInfo project)
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

        FilePath? mongoArchiveFilePath = null;
        FilePath? toolsArchiveFilePath = null;

        try
        {
            // Download MongoDB and its tools
            context.Log.Information("Downloading MongoDB {0}", mongoDownload.Archive.Url);
            context.Log.Information("Downloading MongoDB tools {0}", toolsDownload.Archive.Url);

            var mongoArchiveFilePathTask = Task.Run(() => context.DownloadFile(mongoDownload.Archive.Url));
            var toolsArchiveFilePathTask = Task.Run(() => context.DownloadFile(toolsDownload.Archive.Url));

            mongoArchiveFilePath = await mongoArchiveFilePathTask;
            toolsArchiveFilePath = await toolsArchiveFilePathTask;

            context.Log.Information("MongoDB downloaded to {0}", mongoArchiveFilePath.FullPath);
            context.Log.Information("MongoDB tools downloaded to {0}", toolsArchiveFilePath.FullPath);

            // Verify SHA256 hash
            EnsureHashMatch(context, mongoArchiveFilePath.FullPath, mongoDownload.Archive.Sha256);
            EnsureHashMatch(context, toolsArchiveFilePath.FullPath, toolsDownload.Archive.Sha256);

            // Uncompress archive
            var projectDir = Directory.CreateDirectory(Path.Combine(Constants.RuntimesPath, project.Name, project.Rid, "native", "mongodb"));
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
        finally
        {
            if (mongoArchiveFilePath != null)
            {
                File.Delete(mongoArchiveFilePath.FullPath);
            }

            if (toolsArchiveFilePath != null)
            {
                File.Delete(toolsArchiveFilePath.FullPath);
            }
        }
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

[TaskName("Test")]
[IsDependentOn(typeof(PackTask))]
public sealed class TestTask : FrostingTask<BuildContext>
{
    public override void Run(BuildContext context)
    {
        var source = context.MakeAbsolute(context.File(Constants.OutputDirectoryPath)).FullPath.Replace('/', Path.DirectorySeparatorChar);

        context.DotNetNuGetAddSource("output", new DotNetNuGetSourceSettings
        {
            Source = source
        });

        try
        {
            context.DotNetRemoveReference(Constants.TestProjectPath, Constants.CoreProjectPath);

            var packageNames = new[]
            {
                "Askaiser.EphemeralMongo4",
                "Askaiser.EphemeralMongo5",
                "Askaiser.EphemeralMongo6",
            };

            foreach (var packageName in packageNames)
            {
                context.DotNetAddPackage(Constants.TestProjectPath, packageName, context.PackageVersion);

                try
                {
                    context.DotNetRestore(Constants.TestProjectPath, new DotNetRestoreSettings
                    {
                        Force = true,
                        NoCache = true,
                    });

                    context.DotNetBuild(Constants.TestProjectPath, new DotNetBuildSettings
                    {
                        Configuration = Constants.Release,
                        NoRestore = true,
                        NoLogo = true,
                    });

                    context.DotNetTest(Constants.TestProjectPath, new DotNetTestSettings
                    {
                        Configuration = Constants.Release,
                        NoBuild = true,
                        NoRestore = true,
                        NoLogo = true,
                    });
                }
                finally
                {
                    context.DotNetRemovePackage(Constants.TestProjectPath, packageName);
                }
            }
        }
        finally
        {
            context.DotNetNuGetRemoveSource("output");
            context.DotNetAddReference(Constants.TestProjectPath, Constants.CoreProjectPath);
        }
    }
}

[TaskName("Push")]
[IsDependentOn(typeof(TestTask))]
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
