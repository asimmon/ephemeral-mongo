using System.Runtime.InteropServices;
using EphemeralMongo.Download;

namespace EphemeralMongo;

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

    private static string GetMongoExecutableFileName(Dictionary<OSPlatform, string> mappings) =>
        mappings.Where(x => RuntimeInformation.IsOSPlatform(x.Key)).Select(x => x.Value).FirstOrDefault()
        ?? throw new NotSupportedException("Current operating system is not supported");

    public async Task<string> FindMongoExecutablePathAsync(MongoRunnerOptions options, MongoProcessKind processKind, CancellationToken cancellationToken)
    {
        var executableFileName = AnyMongoExecutableFileNameMappings[processKind];

        if (options.BinaryDirectory != null)
        {
            var userProvidedExecutableFilePath = Path.Combine(options.BinaryDirectory, executableFileName);

            if (!File.Exists(userProvidedExecutableFilePath))
            {
                throw new FileNotFoundException($"The provided binary directory '{options.BinaryDirectory}' does not contain the executable '{executableFileName}'.", userProvidedExecutableFilePath);
            }

            return userProvidedExecutableFilePath;
        }

        if (processKind == MongoProcessKind.Mongod)
        {
            return await MongoExecutableDownloader.DownloadMongodAsync(options, cancellationToken).ConfigureAwait(false);
        }

        var paths = await MongoExecutableDownloader.DownloadMongoToolsAsync(options, cancellationToken).ConfigureAwait(false);

        return processKind == MongoProcessKind.MongoImport ? paths.MongoImportExePath : paths.MongoExportExePath;
    }
}