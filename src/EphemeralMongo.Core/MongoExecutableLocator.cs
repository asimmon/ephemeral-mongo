using System.Runtime.InteropServices;

namespace EphemeralMongo.Core;

internal sealed class MongoExecutableLocator : IMongoExecutableLocator
{
    private static readonly Dictionary<OSPlatform, string> MongodExecutableFileNameMappings = new Dictionary<OSPlatform, string>
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

    private static readonly Dictionary<MongoProcessKind, string> AnyMongoExecutableFileNameMappings = new Dictionary<MongoProcessKind, string>
    {
        [MongoProcessKind.Mongod] = GetMongoExecutableFileName(MongodExecutableFileNameMappings),
        [MongoProcessKind.MongoImport] = GetMongoExecutableFileName(MongoImportExecutableFileNameMappings),
        [MongoProcessKind.MongoExport] = GetMongoExecutableFileName(MongoExportExecutableFileNameMappings),
    };

    private static readonly Dictionary<OSPlatform, string> RidMappings = new Dictionary<OSPlatform, string>
    {
        [OSPlatform.Windows] = "win-x64",
        [OSPlatform.Linux] = "linux-x64",
        [OSPlatform.OSX] = "osx-x64",
    };

    private static string GetMongoExecutableFileName(Dictionary<OSPlatform, string> mappings) =>
        mappings.Where(x => RuntimeInformation.IsOSPlatform(x.Key)).Select(x => x.Value).FirstOrDefault()
        ?? throw new NotSupportedException("Current operating system is not supported");

    public string FindMongoExecutablePath(MongoRunnerOptions options, MongoProcessKind processKind)
    {
        var exploredPaths = new HashSet<string>(StringComparer.Ordinal);
        var exceptions = new List<Exception>();

        var executableFileName = AnyMongoExecutableFileNameMappings[processKind];
        var potentialExecutableFilePaths = GetPotentialMongoExecutablePaths(options, executableFileName);

        foreach (var potentialExecutableFilePath in potentialExecutableFilePaths)
        {
            exploredPaths.Add(potentialExecutableFilePath.FullName);

            try
            {
                if (potentialExecutableFilePath.Exists)
                {
                    return potentialExecutableFilePath.FullName;
                }
            }
            catch (Exception ex)
            {
                exceptions.Add(ex);
            }
        }

        Exception? innerException;

        if (exceptions.Count == 0)
        {
            innerException = null;
        }
        else if (exceptions.Count == 1)
        {
            innerException = exceptions[0];
        }
        else
        {
            innerException = new AggregateException(exceptions);
        }

        throw new FileNotFoundException("Could not find " + executableFileName + " in the following paths: " + string.Join(", ", exploredPaths.Select(x => "'" + x + "'")), executableFileName, innerException);
    }

    // https://stackoverflow.com/questions/52797/how-do-i-get-the-path-of-the-assembly-the-code-is-in
    private static IEnumerable<FileInfo> GetPotentialMongoExecutablePaths(MongoRunnerOptions options, string mongoExecutableFileName)
    {
        if (options.BinaryDirectory != null)
        {
            yield return new FileInfo(Path.Combine(options.BinaryDirectory, mongoExecutableFileName));
            yield break;
        }

        var relativeExecutablePath = Path.Combine("runtimes", GetRid(), "native", "mongodb", "bin", mongoExecutableFileName);

        yield return new FileInfo(Path.Combine(AppContext.BaseDirectory, relativeExecutablePath));

        yield return new FileInfo(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, relativeExecutablePath));

        var uri = new UriBuilder(typeof(MongoExecutableLocator).Assembly.CodeBase);
        if (Path.GetDirectoryName(Uri.UnescapeDataString(uri.Path)) is { } path)
        {
            yield return new FileInfo(Path.Combine(path, relativeExecutablePath));
        }
    }

    private static string GetRid() =>
        RidMappings.Where(x => RuntimeInformation.IsOSPlatform(x.Key)).Select(x => x.Value).FirstOrDefault()
        ?? throw new NotSupportedException("Current operating system is not supported");
}