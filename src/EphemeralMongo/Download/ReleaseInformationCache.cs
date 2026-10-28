using System.Text.Json;

namespace EphemeralMongo.Download;

// MongoDB release information documents are cached on disk and shared by all processes, so their URLs aren't requested again and again,
// such as once per test assembly, MongoDB edition and major version. The download servers sometimes answer with transient errors (503).
internal static class ReleaseInformationCache
{
    private static readonly NamedMutex SharedMutex = new NamedMutex();

    // FetchedAtUtc is null when the download failed and an outdated cached copy was returned instead,
    // so callers don't consider this a successful check and try downloading again next time.
    public static async Task<(TValue Value, DateTime? FetchedAtUtc)> GetAsync<TValue>(HttpTransport transport, string url, string cacheFilePath, TimeSpan maxAge, Logger? logger, CancellationToken cancellationToken)
        where TValue : class
    {
        await SharedMutex.WaitAsync(cacheFilePath, cancellationToken).ConfigureAwait(false);

        try
        {
            // Other processes might be downloading the same document right now. Waiting for them means only one of us sends a request.
            // Reading under the lock also prevents Windows from refusing to replace the cache file because another process has it open.
            using var fileLock = await FileLock.AcquireAsync(cacheFilePath + ".lock", cancellationToken).ConfigureAwait(false);

            var fetchedAtUtc = File.GetLastWriteTimeUtc(cacheFilePath);
            var age = DateTime.UtcNow - fetchedAtUtc;

            // A last write time in the future (clock changes, copied files) would otherwise keep the cache fresh for too long
            if (File.Exists(cacheFilePath) && age >= TimeSpan.Zero && age < maxAge)
            {
                var cachedValue = await TryReadAsync<TValue>(cacheFilePath, cancellationToken).ConfigureAwait(false);
                if (cachedValue != null)
                {
                    return (cachedValue, fetchedAtUtc);
                }
            }

            // Downloaded next to the cache file so that publishing it is an atomic rename. A process crashing mid-download never leaves a partial cache file.
            var tmpFilePath = cacheFilePath + "." + Path.GetRandomFileName() + ".tmp";

            try
            {
                fetchedAtUtc = DateTime.UtcNow;
                await transport.DownloadFileAsync(url, tmpFilePath, cancellationToken).ConfigureAwait(false);

                var value = await TryReadAsync<TValue>(tmpFilePath, cancellationToken).ConfigureAwait(false)
                    ?? throw new EphemeralMongoException($"An error occurred while parsing {url} as a JSON response");

                TryPublish(tmpFilePath, cacheFilePath);
                return (value, fetchedAtUtc);
            }
            catch (EphemeralMongoException ex) when (!cancellationToken.IsCancellationRequested && File.Exists(cacheFilePath))
            {
                // An outdated document is better than failing, as the binaries it points to are most likely already downloaded.
                // Caller cancellations are also wrapped in EphemeralMongoException, and must not be hidden.
                var staleValue = await TryReadAsync<TValue>(cacheFilePath, cancellationToken).ConfigureAwait(false);
                if (staleValue == null)
                {
                    throw;
                }

                logger?.Invoke($"Using the cached copy of {url} last downloaded on {File.GetLastWriteTimeUtc(cacheFilePath):u} because downloading it failed: {ex.Message}");
                return (staleValue, null);
            }
            finally
            {
                DeleteQuietly(tmpFilePath);
            }
        }
        finally
        {
            SharedMutex.Release(cacheFilePath);
        }
    }

    // Returns null when the file is missing, unreadable or isn't a valid document, in which case it's downloaded again
    private static async Task<TValue?> TryReadAsync<TValue>(string filePath, CancellationToken cancellationToken)
        where TValue : class
    {
        try
        {
#if NETSTANDARD2_1_OR_GREATER || NET8_0_OR_GREATER
            var stream = File.OpenRead(filePath);
            await using (stream.ConfigureAwait(false))
#else
            using (var stream = File.OpenRead(filePath))
#endif
            {
                return await JsonSerializer.DeserializeAsync<TValue>(stream, cancellationToken: cancellationToken).ConfigureAwait(false);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            return null;
        }
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
