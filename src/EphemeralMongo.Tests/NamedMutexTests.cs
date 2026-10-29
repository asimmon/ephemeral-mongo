#pragma warning disable CA1849 // CancelAsync isn't available on .NET Framework
using EphemeralMongo.Download;

namespace EphemeralMongo.Tests;

public class NamedMutexTests
{
    [Fact]
    public async Task WaitAsync_Cancelled_DoesNotLeakMutex()
    {
        var namedMutex = new NamedMutex();

        await namedMutex.WaitAsync("foo", CancellationToken.None);

        using (var cts = new CancellationTokenSource())
        {
            var waitTask = namedMutex.WaitAsync("foo", cts.Token);
            Assert.False(waitTask.IsCompleted);

            cts.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => waitTask);
        }

        namedMutex.Release("foo");
        Assert.Equal(0, namedMutex.Count);

        // The mutex can still be acquired after a cancelled wait
        await namedMutex.WaitAsync("foo", CancellationToken.None);
        Assert.Equal(1, namedMutex.Count);
        namedMutex.Release("foo");
        Assert.Equal(0, namedMutex.Count);
    }
}
