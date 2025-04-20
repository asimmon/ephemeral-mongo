using System.Globalization;
using System.Runtime.InteropServices;

namespace EphemeralMongo.Download;

internal static class MongoExecutableDownloader
{
    private const string MongodExeFileNameWithoutExt = "mongod";
    private const string MongoImportExeFileNameWithoutExt = "mongoimport";
    private const string MongoExportExeFileNameWithoutExt = "mongoexport";
    private const string LastCheckFileName = "last-check.txt";

    private static readonly string MongodExeFileName = RuntimeInformation.IsOSPlatform(OSPlatform.Windows) ? MongodExeFileNameWithoutExt + ".exe" : MongodExeFileNameWithoutExt;
    private static readonly string MongoImportExeFileName = RuntimeInformation.IsOSPlatform(OSPlatform.Windows) ? MongoImportExeFileNameWithoutExt + ".exe" : MongoImportExeFileNameWithoutExt;
    private static readonly string MongoExportExeFileName = RuntimeInformation.IsOSPlatform(OSPlatform.Windows) ? MongoExportExeFileNameWithoutExt + ".exe" : MongoExportExeFileNameWithoutExt;

    private static readonly string AppDataDirPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ephemeral-mongo");
    private static readonly string TmpDirPath = Path.Combine(Path.GetTempPath(), "ephemeral-mongo");

    // The epoch for file time is 1/1/1601 12:00:00 AM
    // This value is returned by File.GetLastWriteTimeUtc when the file does not exist
    private static readonly DateTime FileTimeEpochUtc = DateTime.FromFileTimeUtc(0);

    private static readonly NamedMutex SharedMutex = new NamedMutex();

    public static async Task<string> DownloadMongodAsync(MongoRunnerOptions options, CancellationToken cancellationToken)
    {
        var majorVersion = (int)options.Version;
        var edition = DownloadEditionHelper.GetEdition(options.Edition);
        var architecture = DownloadArchitectureHelper.GetArchitecture();
        var target = DownloadTargetHelper.GetTarget(options.Version);

        var baseExeDirName = string.Format(CultureInfo.InvariantCulture, "{0}-{1}-{2}", MongodExeFileNameWithoutExt, edition, majorVersion);
        var baseExeDirPath = Path.Combine(AppDataDirPath, "bin", MongodExeFileNameWithoutExt, baseExeDirName);
        Directory.CreateDirectory(baseExeDirPath);

        var lastCheckFilePath = Path.Combine(baseExeDirPath, LastCheckFileName);
        var lastCheckDateUtc = File.GetLastWriteTimeUtc(lastCheckFilePath);

        var skipNewExeVersionCheck = lastCheckDateUtc > FileTimeEpochUtc && DateTime.UtcNow - lastCheckDateUtc < options.NewVersionCheckTimeout;
        if (skipNewExeVersionCheck)
        {
            var existingExeFilePath = FindLatestExistingMongodExeFilePath(baseExeDirPath);
            if (existingExeFilePath != null)
            {
                return existingExeFilePath;
            }
        }

        await SharedMutex.WaitAsync(baseExeDirName, cancellationToken).ConfigureAwait(false);

        try
        {
            if (skipNewExeVersionCheck)
            {
                var existingExeFilePath = FindLatestExistingMongodExeFilePath(baseExeDirPath);
                if (existingExeFilePath != null)
                {
                    return existingExeFilePath;
                }
            }

            var mongodVersions = await options.Transport.GetFromJsonAsync<MongoVersionsDto>("https://downloads.mongodb.org/current.json", cancellationToken).ConfigureAwait(false);

            var mongodVersion = mongodVersions.Versions.FirstOrDefault(x => x.ProductionRelease && x.Version.StartsWith(majorVersion.ToString(CultureInfo.InvariantCulture), StringComparison.Ordinal))
                ?? throw new EphemeralMongoException($"Could not find a production release for MongoDB major version {majorVersion}");

            var mongodDownload = mongodVersion.Downloads.SingleOrDefault(x => x.Architecture == architecture && x.Edition == edition && x.Target == target)
                ?? throw new EphemeralMongoException($"Could not find a production release for MongoDB architecture {architecture}, edition {edition}, and target {target}");

            var exeDirPath = Path.Combine(baseExeDirPath, mongodVersion.Version);
            var exeFilePath = Path.Combine(exeDirPath, MongodExeFileName);

            if (File.Exists(exeFilePath))
            {
                await UpdateLastCheckFileAsync(lastCheckFilePath).ConfigureAwait(false);
                return exeFilePath;
            }

            var tmpDirName = $"{MongodExeFileNameWithoutExt}-{edition}-{mongodVersion.Version}-{Path.GetRandomFileName()}";
            var tmpDownloadDirPath = Path.Combine(TmpDirPath, "downloads", tmpDirName);
            var tmpUncompressDirPath = Path.Combine(TmpDirPath, "uncompress", tmpDirName);

            Directory.CreateDirectory(tmpDownloadDirPath);
            Directory.CreateDirectory(tmpUncompressDirPath);

            var tmpDownloadFilePath = Path.Combine(tmpDownloadDirPath, Path.GetFileName(mongodDownload.Archive.Url));

            try
            {
                await options.Transport.DownloadFileAsync(mongodDownload.Archive.Url, tmpDownloadFilePath, cancellationToken).ConfigureAwait(false);
                await FileHashHelper.EnsureFileSha256HashAsync(tmpDownloadFilePath, mongodDownload.Archive.Sha256, cancellationToken).ConfigureAwait(false);
                await FileCompressionHelper.ExtractToDirectoryAsync(tmpDownloadFilePath, tmpUncompressDirPath, cancellationToken).ConfigureAwait(false);

                var tmpUncompressedFirstDirPath = Directory.GetDirectories(tmpUncompressDirPath);
                if (tmpUncompressedFirstDirPath.Length != 1)
                {
                    throw new EphemeralMongoException($"There should be only one directory in {tmpUncompressDirPath}, but found {tmpUncompressedFirstDirPath.Length}");
                }

                var tmpExeDirPath = Path.Combine(tmpUncompressedFirstDirPath[0], "bin");
                var tmpExeFilePath = Path.Combine(tmpExeDirPath, MongodExeFileName);
                if (!File.Exists(tmpExeFilePath))
                {
                    throw new EphemeralMongoException($"The executable file {tmpExeFilePath} could not be copied to {exeFilePath} because it does not exist");
                }

                Directory.CreateDirectory(exeDirPath);

                SafeFileCopy(tmpExeFilePath, exeFilePath);

                if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
                {
                    // On Windows only as of 2025-04-08, the enterprise edition requires additional DLLs
                    foreach (var tmpDllFilePath in Directory.EnumerateFiles(tmpExeDirPath, "*.dll", SearchOption.TopDirectoryOnly))
                    {
                        var dllFilePath = Path.Combine(exeDirPath, Path.GetFileName(tmpDllFilePath));
                        SafeFileCopy(tmpDllFilePath, dllFilePath);
                    }
                }

                await UpdateLastCheckFileAsync(lastCheckFilePath).ConfigureAwait(false);
                return exeFilePath;
            }
            finally
            {
                DeleteQuietly(tmpDownloadDirPath);
                DeleteQuietly(tmpUncompressDirPath);
            }
        }
        finally
        {
            SharedMutex.Release(baseExeDirName);
        }
    }

    public static async Task<(string MongoImportExePath, string MongoExportExePath)> DownloadMongoToolsAsync(MongoRunnerOptions options, CancellationToken cancellationToken)
    {
        const string baseExeDirName = "tools";

        var baseExeDirPath = Path.Combine(AppDataDirPath, "bin", baseExeDirName);
        Directory.CreateDirectory(baseExeDirPath);

        var lastCheckFilePath = Path.Combine(baseExeDirPath, LastCheckFileName);
        var lastCheckDateUtc = File.GetLastWriteTimeUtc(lastCheckFilePath);

        var skipNewExeVersionCheck = lastCheckDateUtc > FileTimeEpochUtc && DateTime.UtcNow - lastCheckDateUtc < options.NewVersionCheckTimeout;
        if (skipNewExeVersionCheck)
        {
            var existingExeFilePath = FindLatestExistingToolExeFilePaths(baseExeDirPath);
            if (existingExeFilePath.HasValue)
            {
                return existingExeFilePath.Value;
            }
        }

        await SharedMutex.WaitAsync(baseExeDirName, cancellationToken).ConfigureAwait(false);

        try
        {
            if (skipNewExeVersionCheck)
            {
                var existingExeFilePath = FindLatestExistingToolExeFilePaths(baseExeDirPath);
                if (existingExeFilePath.HasValue)
                {
                    return existingExeFilePath.Value;
                }
            }

            var architecture = DownloadArchitectureHelper.GetArchitecture();
            var target = DownloadTargetHelper.GetTarget(options.Version);

            var mongoToolsVersions = await options.Transport.GetFromJsonAsync<ToolsVersionsDto>("https://downloads.mongodb.org/tools/db/release.json", cancellationToken).ConfigureAwait(false);

            var mongoToolsVersion = mongoToolsVersions.Versions.FirstOrDefault()
                ?? throw new EphemeralMongoException($"Could not find the latest MongoDB tools version");

            var mongoToolsDownload = mongoToolsVersion.Downloads.SingleOrDefault(x => x.Architecture == architecture && x.Name == target)
                ?? throw new EphemeralMongoException($"Could not find the latest MongoDB tools version for architecture {architecture} and target {target}");

            var exeDirPath = Path.Combine(baseExeDirPath, mongoToolsVersion.Version);
            var mongoImportExeFilePath = Path.Combine(exeDirPath, MongoImportExeFileName);
            var mongoExportExeFilePath = Path.Combine(exeDirPath, MongoExportExeFileName);

            if (File.Exists(mongoImportExeFilePath) && File.Exists(mongoExportExeFilePath))
            {
                await UpdateLastCheckFileAsync(lastCheckFilePath).ConfigureAwait(false);
                return (mongoImportExeFilePath, mongoExportExeFilePath);
            }

            var tmpDirName = $"tools-{mongoToolsVersion.Version}-{Path.GetRandomFileName()}";
            var tmpDownloadDirPath = Path.Combine(TmpDirPath, "downloads", tmpDirName);
            var tmpUncompressDirPath = Path.Combine(TmpDirPath, "uncompress", tmpDirName);

            Directory.CreateDirectory(exeDirPath);
            Directory.CreateDirectory(tmpDownloadDirPath);
            Directory.CreateDirectory(tmpUncompressDirPath);

            var tmpDownloadFilePath = Path.Combine(tmpDownloadDirPath, Path.GetFileName(mongoToolsDownload.Archive.Url));

            try
            {
                await options.Transport.DownloadFileAsync(mongoToolsDownload.Archive.Url, tmpDownloadFilePath, cancellationToken).ConfigureAwait(false);
                await FileHashHelper.EnsureFileSha256HashAsync(tmpDownloadFilePath, mongoToolsDownload.Archive.Sha256, cancellationToken).ConfigureAwait(false);
                await FileCompressionHelper.ExtractToDirectoryAsync(tmpDownloadFilePath, tmpUncompressDirPath, cancellationToken).ConfigureAwait(false);

                var tmpUncompressedFirstDirPath = Directory.GetDirectories(tmpUncompressDirPath);
                if (tmpUncompressedFirstDirPath.Length != 1)
                {
                    throw new EphemeralMongoException($"There should be only one directory in {tmpUncompressDirPath}, but found {tmpUncompressedFirstDirPath.Length}");
                }

                var tmpMongoImportExeFilePath = Path.Combine(tmpUncompressedFirstDirPath[0], "bin", MongoImportExeFileName);
                var tmpMongoExportExeFilePath = Path.Combine(tmpUncompressedFirstDirPath[0], "bin", MongoExportExeFileName);

                if (!File.Exists(tmpMongoImportExeFilePath) || !File.Exists(tmpMongoExportExeFilePath))
                {
                    throw new EphemeralMongoException($"The executable files {tmpMongoImportExeFilePath} and {tmpMongoExportExeFilePath} could not be copied to {exeDirPath} because they do not exist");
                }

                SafeFileCopy(tmpMongoImportExeFilePath, mongoImportExeFilePath);
                SafeFileCopy(tmpMongoExportExeFilePath, mongoExportExeFilePath);

                await UpdateLastCheckFileAsync(lastCheckFilePath).ConfigureAwait(false);
                return (mongoImportExeFilePath, mongoExportExeFilePath);
            }
            finally
            {
                DeleteQuietly(tmpDownloadDirPath);
                DeleteQuietly(tmpUncompressDirPath);
            }
        }
        finally
        {
            SharedMutex.Release(baseExeDirName);
        }
    }

    private static async Task UpdateLastCheckFileAsync(string lastCheckFilePath)
    {
        const int maxAttempts = 3;
        const int retryDelayMs = 50;

        for (var attempt = 1; attempt <= maxAttempts; attempt++)
        {
            try
            {
                // An IO conflict happened in Windows CI where the file was edited by another process (multi-assembly testing)
#if NETSTANDARD2_0
                File.Create(lastCheckFilePath).Dispose();
#else
                await File.Create(lastCheckFilePath).DisposeAsync().ConfigureAwait(false);
#endif
                return;
            }
            catch (IOException)
            {
                if (attempt == maxAttempts)
                {
                    throw;
                }

                await Task.Delay(retryDelayMs).ConfigureAwait(false);
            }
        }
    }

    private static string? FindLatestExistingMongodExeFilePath(string baseExeDirPath)
    {
        var latestVersion = FindLatestDirectoryVersion(baseExeDirPath);
        if (latestVersion == null)
        {
            return null;
        }

        var exeDirPath = Path.Combine(baseExeDirPath, latestVersion.ToString());
        var exeFilePath = Path.Combine(exeDirPath, MongodExeFileName);

        if (File.Exists(exeFilePath))
        {
            return exeFilePath;
        }

        try
        {
            Directory.Delete(exeDirPath);
        }
        catch (IOException ex)
        {
            throw new EphemeralMongoException($"The directory {exeDirPath} did not contain the expected executable file {MongodExeFileName} and could not be deleted. Please delete it first.", ex);
        }

        return null;
    }

    private static (string MongoImportExePath, string MongoExportExePath)? FindLatestExistingToolExeFilePaths(string baseExeDirPath)
    {
        var latestVersion = FindLatestDirectoryVersion(baseExeDirPath);
        if (latestVersion == null)
        {
            return null;
        }

        var exeDirPath = Path.Combine(baseExeDirPath, latestVersion.ToString());
        var mongoImportexeFilePath = Path.Combine(exeDirPath, MongoImportExeFileName);
        var mongoExportexeFilePath = Path.Combine(exeDirPath, MongoExportExeFileName);

        if (File.Exists(mongoImportexeFilePath) && File.Exists(mongoExportexeFilePath))
        {
            return (mongoImportexeFilePath, mongoExportexeFilePath);
        }

        try
        {
            Directory.Delete(exeDirPath);
        }
        catch (IOException ex)
        {
            throw new EphemeralMongoException($"The directory {exeDirPath} did not contain the expected executable files {MongoImportExeFileName} and {MongoExportExeFileName} and could not be deleted. Please delete it first.", ex);
        }

        return null;
    }

    private static Version? FindLatestDirectoryVersion(string baseExeDirPath)
    {
        Version? latestVersion = null;
        foreach (var dirPath in Directory.EnumerateDirectories(baseExeDirPath, "*", SearchOption.TopDirectoryOnly))
        {
            if (Version.TryParse(Path.GetFileName(dirPath), out var version) && (latestVersion == null || version > latestVersion))
            {
                latestVersion = version;
            }
        }

        return latestVersion;
    }

    private static void DeleteQuietly(string path)
    {
        try
        {
            Directory.Delete(path, recursive: true);
        }
        catch (IOException)
        {
            // We did our best. OS can clean up later.
        }
    }

    private static void SafeFileCopy(string sourceFilePath, string destFilePath)
    {
        if (File.Exists(destFilePath))
        {
            return;
        }

        try
        {
            File.Copy(sourceFilePath, destFilePath, overwrite: false);
        }
        catch (IOException) when (File.Exists(destFilePath))
        {
            // Another process already copied the file, which is fine
            // This mostly happens in tests where we run two assemblies side by side (net9.0 and net472 for instance)
        }
    }
}
