using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;

namespace Askaiser.EphemeralMongo.Core;

internal sealed class MongoExecutableLocator : IMongoExecutableLocator
{
    private static readonly Dictionary<OSPlatform, string> MongoExecutableFileNameMappings = new Dictionary<OSPlatform, string>
    {
        [OSPlatform.Windows] = "mongod.exe",
        [OSPlatform.Linux] = "mongod",
        [OSPlatform.OSX] = "mongod",
    };

    private static readonly Dictionary<OSPlatform, string> MongoImportExecutableFileNameMappings = new Dictionary<OSPlatform, string>
    {
        [OSPlatform.Windows] = "mongoimport.exe",
        [OSPlatform.Linux] = "mongoimport",
        [OSPlatform.OSX] = "mongoimport",
    };

    private static readonly Dictionary<OSPlatform, string> MongoExportExecutableFileNameMappings = new Dictionary<OSPlatform, string>
    {
        [OSPlatform.Windows] = "mongoexport.exe",
        [OSPlatform.Linux] = "mongoexport",
        [OSPlatform.OSX] = "mongoexport",
    };

    private static readonly Dictionary<OSPlatform, string> RidMappings = new Dictionary<OSPlatform, string>
    {
        [OSPlatform.Windows] = "win-x64",
        [OSPlatform.Linux] = "osx-x64",
        [OSPlatform.OSX] = "linux-x64",
    };

    public string? FindMongoExecutablePath() => GetPotentialMongoExecutablePaths(GetMongoExecutableFileName()).Where(x => x.Exists).Select(x => x.FullName).FirstOrDefault();

    public string? FindMongoImportExecutablePath() => GetPotentialMongoExecutablePaths(GetMongoImportExecutableFileName()).Where(x => x.Exists).Select(x => x.FullName).FirstOrDefault();

    public string? FindMongoExportExecutablePath() => GetPotentialMongoExecutablePaths(GetMongoExportExecutableFileName()).Where(x => x.Exists).Select(x => x.FullName).FirstOrDefault();

    private static string GetMongoExecutableFileName() =>
        MongoExecutableFileNameMappings.Where(x => RuntimeInformation.IsOSPlatform(x.Key)).Select(x => x.Value).FirstOrDefault()
        ?? throw new NotSupportedException("Current operating system is not supported");

    private static string GetMongoImportExecutableFileName() =>
        MongoImportExecutableFileNameMappings.Where(x => RuntimeInformation.IsOSPlatform(x.Key)).Select(x => x.Value).FirstOrDefault()
        ?? throw new NotSupportedException("Current operating system is not supported");

    private static string GetMongoExportExecutableFileName() =>
        MongoExportExecutableFileNameMappings.Where(x => RuntimeInformation.IsOSPlatform(x.Key)).Select(x => x.Value).FirstOrDefault()
        ?? throw new NotSupportedException("Current operating system is not supported");

    private static string GetRid() =>
        RidMappings.Where(x => RuntimeInformation.IsOSPlatform(x.Key)).Select(x => x.Value).FirstOrDefault()
        ?? throw new NotSupportedException("Current operating system is not supported");

    // https://stackoverflow.com/questions/52797/how-do-i-get-the-path-of-the-assembly-the-code-is-in
    private static IEnumerable<FileInfo> GetPotentialMongoExecutablePaths(string mongoExecutableFileName)
    {
        var relativeExecutablePath = Path.Combine("runtimes", GetRid(), "native", "mongodb", "bin", mongoExecutableFileName);

        yield return new FileInfo(Path.Combine(AppContext.BaseDirectory, relativeExecutablePath));

        yield return new FileInfo(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, relativeExecutablePath));

        var uri = new UriBuilder(typeof(MongoExecutableLocator).Assembly.CodeBase);
        if (Path.GetDirectoryName(Uri.UnescapeDataString(uri.Path)) is { } path)
        {
            yield return new FileInfo(Path.Combine(path, relativeExecutablePath));
        }
    }
}