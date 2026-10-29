namespace EphemeralMongo.Download;

// Serializes work across processes, such as multiple test assemblies downloading the same MongoDB binaries side by side.
// On Unix, .NET emulates FileShare.None with an advisory flock() that other .NET processes honor.
internal sealed class FileLock : IDisposable
{
    private static readonly TimeSpan RetryDelay = TimeSpan.FromMilliseconds(100);

    // Generous enough to cover another process downloading and extracting large archives on a slow connection
    public static readonly TimeSpan DefaultMaxWaitTime = TimeSpan.FromMinutes(10);

    private readonly FileStream _stream;

    private FileLock(FileStream stream)
    {
        this._stream = stream;
    }

    public static Task<FileLock> AcquireAsync(string lockFilePath, CancellationToken cancellationToken)
    {
        return AcquireAsync(lockFilePath, DefaultMaxWaitTime, cancellationToken);
    }

    public static async Task<FileLock> AcquireAsync(string lockFilePath, TimeSpan maxWaitTime, CancellationToken cancellationToken)
    {
        var startedAtUtc = DateTime.UtcNow;

        while (true)
        {
            try
            {
                // The lock file is never deleted, otherwise waiting processes could end up locking different files
                return new FileLock(new FileStream(lockFilePath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None));
            }
            catch (IOException ex)
            {
                if (DateTime.UtcNow - startedAtUtc > maxWaitTime)
                {
                    throw new EphemeralMongoException($"Could not acquire the lock file {lockFilePath} within {maxWaitTime.TotalSeconds} seconds. Another process might be stuck while holding it.", ex);
                }

                await Task.Delay(RetryDelay, cancellationToken).ConfigureAwait(false);
            }
        }
    }

    public void Dispose()
    {
        this._stream.Dispose();
    }
}
