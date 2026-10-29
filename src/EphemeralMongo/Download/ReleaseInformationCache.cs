using System.Globalization;
using System.Text.Json;

namespace EphemeralMongo.Download;

// MongoDB release information documents are cached on disk and shared by all processes, so their URLs aren't requested again and again,
// such as once per test assembly, MongoDB edition and major version. The download servers sometimes answer with transient errors (503).
internal static class ReleaseInformationCache
{
    internal const string MongodReleasesUrl = "https://downloads.mongodb.org/current.json";
    internal const string ToolsReleasesUrl = "https://downloads.mongodb.org/tools/db/release.json";

    // These documents are small, so each attempt must complete quickly. A hanging server would otherwise hold the lock for minutes.
    private static readonly TimeSpan DownloadAttemptTimeout = TimeSpan.FromSeconds(30);

    // After a failed download, the outdated copy is used for a while instead of sending more requests to the struggling download servers
    private static readonly TimeSpan FailedDownloadRetryDelay = TimeSpan.FromMinutes(5);

    private static readonly NamedMutex SharedMutex = new NamedMutex();

    // A healthy download only holds the lock for a second or two, so a longer wait means the lock holder is struggling with the download servers.
    // Internal for testing.
    internal static TimeSpan StaleCopyLockWaitTime { get; set; } = TimeSpan.FromSeconds(30);

    // FetchedAtUtc is null when the download failed and an outdated cached copy was returned instead,
    // so callers don't consider this a successful check and try downloading again next time.
    public static Task<(MongoVersionsDto Versions, DateTime? FetchedAtUtc)> GetMongodVersionsAsync(MongoRunnerOptions options, string cacheFilePath, CancellationToken cancellationToken)
    {
        return GetAsync<MongoVersionsDto>(options, MongodReleasesUrl, cacheFilePath, HasMongodVersions, cancellationToken);
    }

    public static Task<(ToolsVersionsDto Versions, DateTime? FetchedAtUtc)> GetToolsVersionsAsync(MongoRunnerOptions options, string cacheFilePath, CancellationToken cancellationToken)
    {
        return GetAsync<ToolsVersionsDto>(options, ToolsReleasesUrl, cacheFilePath, HasToolsVersions, cancellationToken);
    }

    // A parsable document can still be unusable, such as an empty object
    private static bool HasMongodVersions(MongoVersionsDto dto) => dto.Versions is { Length: > 0 };

    private static bool HasToolsVersions(ToolsVersionsDto dto) => dto.Versions is { Length: > 0 };

    private static async Task<(TValue Value, DateTime? FetchedAtUtc)> GetAsync<TValue>(MongoRunnerOptions options, string url, string cacheFilePath, Func<TValue, bool> isValid, CancellationToken cancellationToken)
        where TValue : class
    {
        await SharedMutex.WaitAsync(cacheFilePath, cancellationToken).ConfigureAwait(false);

        try
        {
            var lockFilePath = cacheFilePath + ".lock";
            FileLock fileLock;

            try
            {
                // Other processes might be downloading the same document right now. Waiting for them means only one of us sends a request.
                fileLock = await FileLock.AcquireAsync(lockFilePath, File.Exists(cacheFilePath) ? StaleCopyLockWaitTime : FileLock.DefaultMaxWaitTime, cancellationToken).ConfigureAwait(false);
            }
            catch (EphemeralMongoException ex)
            {
                // Reading without the lock might fail if the lock holder is replacing the file right now, in which case we keep waiting
                var staleValue = await TryReadAsync(cacheFilePath, isValid, cancellationToken).ConfigureAwait(false);
                if (staleValue != null)
                {
                    LogStaleCopyUsage(options.StandardErrorLogger, url, cacheFilePath, "another process has been downloading it for too long: " + ex.Message);
                    return (staleValue, null);
                }

                fileLock = await FileLock.AcquireAsync(lockFilePath, cancellationToken).ConfigureAwait(false);
            }

            using (fileLock)
            {
                return await GetWhileLockedAsync(options.Transport, url, cacheFilePath, options.NewVersionCheckTimeout, isValid, options.StandardErrorLogger, cancellationToken).ConfigureAwait(false);
            }
        }
        finally
        {
            SharedMutex.Release(cacheFilePath);
        }
    }

    private static async Task<(TValue Value, DateTime? FetchedAtUtc)> GetWhileLockedAsync<TValue>(HttpTransport transport, string url, string cacheFilePath, TimeSpan maxAge, Func<TValue, bool> isValid, Logger? logger, CancellationToken cancellationToken)
        where TValue : class
    {
        // Temporary files are only written while holding the lock, so any remaining one was left by a process that was killed
        DeleteTemporaryFiles(cacheFilePath);

        // Reading under the lock prevents Windows from refusing to replace the cache file because another process has it open
        if (IsYoungerThan(cacheFilePath, maxAge, out var fetchedAtUtc))
        {
            var cachedValue = await TryReadAsync(cacheFilePath, isValid, cancellationToken).ConfigureAwait(false);
            if (cachedValue != null)
            {
                return (cachedValue, fetchedAtUtc);
            }
        }

        var failedMarkerFilePath = cacheFilePath + ".failed";
        if (IsYoungerThan(failedMarkerFilePath, FailedDownloadRetryDelay, out _))
        {
            var staleValue = await TryReadAsync(cacheFilePath, isValid, cancellationToken).ConfigureAwait(false);
            if (staleValue != null)
            {
                LogStaleCopyUsage(logger, url, cacheFilePath, string.Format(CultureInfo.InvariantCulture, "downloading it failed less than {0} minutes ago", FailedDownloadRetryDelay.TotalMinutes));
                return (staleValue, null);
            }
        }

        // Downloaded next to the cache file so that publishing it is an atomic rename. A process crashing mid-download never leaves a partial cache file.
        var tmpFilePath = cacheFilePath + "." + Path.GetRandomFileName() + ".tmp";

        try
        {
            fetchedAtUtc = DateTime.UtcNow;
            await transport.DownloadFileAsync(url, tmpFilePath, DownloadAttemptTimeout, cancellationToken).ConfigureAwait(false);

            TValue value;
            try
            {
                value = await ReadAsync(tmpFilePath, isValid, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or InvalidDataException)
            {
                throw new EphemeralMongoException($"An error occurred while parsing {url} as a JSON response", ex);
            }

            TryPublish(tmpFilePath, cacheFilePath);
            DeleteQuietly(failedMarkerFilePath);
            return (value, fetchedAtUtc);
        }
        catch (EphemeralMongoException ex) when (!cancellationToken.IsCancellationRequested && File.Exists(cacheFilePath))
        {
            // An outdated document is better than failing, as the binaries it points to are most likely already downloaded.
            // Caller cancellations are also wrapped in EphemeralMongoException, and must not be hidden.
            var staleValue = await TryReadAsync(cacheFilePath, isValid, cancellationToken).ConfigureAwait(false);
            if (staleValue == null)
            {
                throw;
            }

            TryMarkAsFailed(failedMarkerFilePath);
            LogStaleCopyUsage(logger, url, cacheFilePath, "downloading it failed: " + ex.GetBaseException().Message);
            return (staleValue, null);
        }
        finally
        {
            DeleteQuietly(tmpFilePath);
        }
    }

    private static bool IsYoungerThan(string filePath, TimeSpan maxAge, out DateTime lastWriteTimeUtc)
    {
        lastWriteTimeUtc = File.GetLastWriteTimeUtc(filePath);
        var age = DateTime.UtcNow - lastWriteTimeUtc;

        // A last write time in the future (clock changes, copied files) would otherwise keep the file fresh for too long
        return File.Exists(filePath) && age >= TimeSpan.Zero && age < maxAge;
    }

    private static async Task<TValue> ReadAsync<TValue>(string filePath, Func<TValue, bool> isValid, CancellationToken cancellationToken)
        where TValue : class
    {
        TValue? value;

#if NETSTANDARD2_1_OR_GREATER || NET8_0_OR_GREATER
        var stream = File.OpenRead(filePath);
        await using (stream.ConfigureAwait(false))
#else
        using (var stream = File.OpenRead(filePath))
#endif
        {
            value = await JsonSerializer.DeserializeAsync<TValue>(stream, cancellationToken: cancellationToken).ConfigureAwait(false);
        }

        return value != null && isValid(value) ? value : throw new InvalidDataException("The document doesn't contain any release information");
    }

    // Returns null when the file is missing, unreadable or isn't a valid document, in which case it's downloaded again
    private static async Task<TValue?> TryReadAsync<TValue>(string filePath, Func<TValue, bool> isValid, CancellationToken cancellationToken)
        where TValue : class
    {
        try
        {
            return await ReadAsync(filePath, isValid, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or InvalidDataException)
        {
            return null;
        }
    }

    private static void LogStaleCopyUsage(Logger? logger, string url, string cacheFilePath, string reason)
    {
        logger?.Invoke($"Using the cached copy of {url} last downloaded on {File.GetLastWriteTimeUtc(cacheFilePath):u} because {reason}");
    }

    private static void TryPublish(string tmpFilePath, string cacheFilePath)
    {
        try
        {
#if NET8_0_OR_GREATER
            File.Move(tmpFilePath, cacheFilePath, overwrite: true);
#else
            if (File.Exists(cacheFilePath))
            {
                File.Replace(tmpFilePath, cacheFilePath, destinationBackupFileName: null);
            }
            else
            {
                File.Move(tmpFilePath, cacheFilePath);
            }
#endif
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Caching is best effort. Antivirus software sometimes holds files open on Windows. The next caller will download the document again.
        }
    }

    private static void TryMarkAsFailed(string failedMarkerFilePath)
    {
        try
        {
            File.Create(failedMarkerFilePath).Dispose();

            // Truncating an empty file doesn't always update its last write time
            File.SetLastWriteTimeUtc(failedMarkerFilePath, DateTime.UtcNow);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Without the marker, the next caller will try downloading again, which is fine
        }
    }

    private static void DeleteTemporaryFiles(string cacheFilePath)
    {
        try
        {
            var dirPath = Path.GetDirectoryName(cacheFilePath)!;
            foreach (var tmpFilePath in Directory.EnumerateFiles(dirPath, Path.GetFileName(cacheFilePath) + ".*.tmp", SearchOption.TopDirectoryOnly))
            {
                DeleteQuietly(tmpFilePath);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // We did our best. These leftover files are harmless.
        }
    }

    private static void DeleteQuietly(string filePath)
    {
        try
        {
            File.Delete(filePath);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // We did our best. This leftover file is harmless.
        }
    }
}
