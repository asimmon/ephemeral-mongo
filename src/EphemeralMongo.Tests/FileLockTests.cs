using EphemeralMongo.Download;

namespace EphemeralMongo.Tests;

public class FileLockTests(ITestContextAccessor testContextAccessor)
{
    [Fact]
    public async Task AcquireAsync_WhenAlreadyHeld_WaitsUntilReleased()
    {
        var lockFilePath = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());

        try
        {
            var firstLock = await FileLock.AcquireAsync(lockFilePath, testContextAccessor.Current.CancellationToken);
            var secondLockTask = FileLock.AcquireAsync(lockFilePath, testContextAccessor.Current.CancellationToken);

            try
            {
                await Task.Delay(TimeSpan.FromMilliseconds(500), testContextAccessor.Current.CancellationToken);
                Assert.False(secondLockTask.IsCompleted, "The lock should not be acquired while another holder has it.");
            }
            finally
            {
                firstLock.Dispose();
            }

            using (await secondLockTask)
            {
            }
        }
        finally
        {
            File.Delete(lockFilePath);
        }
    }

    [Fact]
    public async Task AcquireAsync_WhenCancelledWhileWaiting_Throws()
    {
        var lockFilePath = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());

        try
        {
            using (await FileLock.AcquireAsync(lockFilePath, testContextAccessor.Current.CancellationToken))
            using (var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(300)))
            {
                await Assert.ThrowsAnyAsync<OperationCanceledException>(() => FileLock.AcquireAsync(lockFilePath, cts.Token));
            }
        }
        finally
        {
            File.Delete(lockFilePath);
        }
    }
}
