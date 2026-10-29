#if NET
using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;

namespace EphemeralMongo.Tests;

public class OrphanProtectionTests(ITestOutputHelper testOutputHelper, ITestContextAccessor testContextAccessor)
{
    private static readonly TimeSpan ExitTimeout = TimeSpan.FromSeconds(15);

    private static bool IsWindows => RuntimeInformation.IsOSPlatform(OSPlatform.Windows);

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task KillingTheHostProcess_StopsMongod(bool replset)
    {
        Assert.SkipWhen(IsWindows, "Windows relies on a job object, the guardian is only used on Linux and macOS");

        var hostPath = Path.Combine(AppContext.BaseDirectory, "EphemeralMongo.OrphanTestHost.dll");
        var startInfo = new ProcessStartInfo(Environment.GetEnvironmentVariable("DOTNET_HOST_PATH") ?? "dotnet")
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        startInfo.ArgumentList.Add(hostPath);
        if (replset)
        {
            startInfo.ArgumentList.Add("--replset");
        }

        using var host = Process.Start(startInfo)!;
        var ready = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        host.OutputDataReceived += (_, e) =>
        {
            if (e.Data == "READY")
            {
                ready.TrySetResult(true);
            }
        };
        host.ErrorDataReceived += (_, e) => testOutputHelper.WriteLine("host: {0}", e.Data ?? string.Empty);
        host.BeginOutputReadLine();
        host.BeginErrorReadLine();

        try
        {
            await ready.Task.WaitAsync(TimeSpan.FromMinutes(2), testContextAccessor.Current.CancellationToken);

            var mongodProcessId = FindChildMongodProcessId(host.Id);
            var dataDirectory = ReadDataDirectory(mongodProcessId);
            testOutputHelper.WriteLine("Host {0} runs mongod {1} on {2}", host.Id, mongodProcessId, dataDirectory);
            Assert.True(Directory.Exists(dataDirectory), $"The data directory {dataDirectory} should exist while mongod runs");
            using var mongod = Process.GetProcessById(mongodProcessId);

            // SIGKILL: no managed code runs, Dispose never happens
            host.Kill();
            await host.WaitForExitAsync(testContextAccessor.Current.CancellationToken);

            Assert.True(await this.WaitForExitAsync(mongod), $"mongod {mongodProcessId} kept running after its host was killed");
            await this.WaitUntilAsync(() => !Directory.Exists(dataDirectory), $"data directory {dataDirectory} was not removed");
        }
        finally
        {
            if (!host.HasExited)
            {
                host.Kill();
            }
        }
    }

    [Fact]
    public async Task Guardian_WhenOwnerExits_KillsProcessAndDeletesDirectory()
    {
        Assert.SkipWhen(IsWindows, "The guardian is only used on Linux and macOS");

        var directory = CreateTemporaryDirectory();
        using var target = StartMarkedProcess(directory);
        using var guardian = UnixProcessGuardian.TryStart(target.Id, directory, commandLineMarker: directory, errorLogger: null);
        Assert.NotNull(guardian);

        guardian.SimulateOwnerExit();

        Assert.True(await this.WaitForExitAsync(target), "The guarded process should be killed");
        Assert.True(guardian.WaitForExit(ExitTimeout), "The guardian should exit");
        await this.WaitUntilAsync(() => !Directory.Exists(directory), "The data directory should be removed");
    }

    [Fact]
    public async Task Guardian_WhenDisposed_LeavesProcessAndDirectory()
    {
        Assert.SkipWhen(IsWindows, "The guardian is only used on Linux and macOS");

        var directory = CreateTemporaryDirectory();
        using var target = StartMarkedProcess(directory);

        try
        {
            var guardian = UnixProcessGuardian.TryStart(target.Id, directory, commandLineMarker: directory, errorLogger: null);
            Assert.NotNull(guardian);
            guardian.Dispose();

            await Task.Delay(TimeSpan.FromSeconds(1), testContextAccessor.Current.CancellationToken);
            Assert.False(target.HasExited, "A normal dispose must not kill the process");
            Assert.True(Directory.Exists(directory), "A normal dispose must not remove the directory");
        }
        finally
        {
            KillQuietly(target);
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task Guardian_WhenCommandLineDoesNotMatch_DoesNotKillReusedProcessId()
    {
        Assert.SkipWhen(IsWindows, "The guardian is only used on Linux and macOS");

        var directory = CreateTemporaryDirectory();
        using var target = StartMarkedProcess(directory);

        try
        {
            // Simulates mongod having exited on its own and its pid being reused by an unrelated process
            using var guardian = UnixProcessGuardian.TryStart(target.Id, directory, commandLineMarker: "some-other-data-directory", errorLogger: null);
            Assert.NotNull(guardian);

            guardian.SimulateOwnerExit();
            Assert.True(guardian.WaitForExit(ExitTimeout), "The guardian should exit");

            await Task.Delay(TimeSpan.FromMilliseconds(500), testContextAccessor.Current.CancellationToken);
            Assert.False(target.HasExited, "An unrelated process must not be killed");
            Assert.True(Directory.Exists(directory), "The directory must not be removed when the process is not ours");
        }
        finally
        {
            KillQuietly(target);
            Directory.Delete(directory, recursive: true);
        }
    }

    // mongod is started directly by the host, so it is the host's only mongod child
    private static int FindChildMongodProcessId(int hostProcessId)
    {
        var output = RunAndReadOutput("pgrep", "-P", hostProcessId.ToString(CultureInfo.InvariantCulture), "mongod");
        var processIds = output.Split(['\n', '\r'], StringSplitOptions.RemoveEmptyEntries);
        var processId = Assert.Single(processIds);
        return int.Parse(processId, CultureInfo.InvariantCulture);
    }

    // The arguments are built by MongoRunner as "--dbpath <directory> --port <port> ...", and ps prints them unquoted
    private static string ReadDataDirectory(int mongodProcessId)
    {
        const string dbPathArgument = "--dbpath ";
        const string portArgument = " --port ";

        var commandLine = RunAndReadOutput("ps", "-o", "args=", "-p", mongodProcessId.ToString(CultureInfo.InvariantCulture)).Trim();
        var start = commandLine.IndexOf(dbPathArgument, StringComparison.Ordinal);
        var end = commandLine.IndexOf(portArgument, StringComparison.Ordinal);
        Assert.True(start >= 0 && end > start, $"Could not find the data directory in the mongod command line '{commandLine}'");

        start += dbPathArgument.Length;
        return commandLine.Substring(start, end - start);
    }

    private static string RunAndReadOutput(string fileName, params string[] arguments)
    {
        var startInfo = new ProcessStartInfo(fileName)
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
        };

        foreach (var argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        using var process = Process.Start(startInfo)!;
        var output = process.StandardOutput.ReadToEnd();
        process.WaitForExit();
        return output;
    }

    private static string CreateTemporaryDirectory()
    {
        var directory = Path.Combine(Path.GetTempPath(), "ephemeral-mongo-guardian-test-" + Path.GetRandomFileName());
        Directory.CreateDirectory(directory);
        return directory;
    }

    // A long-running process whose command line contains the directory, like mongod's --dbpath argument.
    // The loop keeps /bin/sh itself running instead of exec-ing into sleep, so the pid stays the one we guard.
    private static Process StartMarkedProcess(string directory)
    {
        var startInfo = new ProcessStartInfo("/bin/sh") { UseShellExecute = false };
        startInfo.ArgumentList.Add("-c");
        startInfo.ArgumentList.Add("while :; do sleep 0.2; done");
        startInfo.ArgumentList.Add("guarded-process");
        startInfo.ArgumentList.Add(directory);
        return Process.Start(startInfo)!;
    }

    private async Task<bool> WaitForExitAsync(Process process)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(testContextAccessor.Current.CancellationToken);
        timeout.CancelAfter(ExitTimeout);

        try
        {
            await process.WaitForExitAsync(timeout.Token);
            return true;
        }
        catch (OperationCanceledException) when (!testContextAccessor.Current.CancellationToken.IsCancellationRequested)
        {
            return false;
        }
    }

    private async Task WaitUntilAsync(Func<bool> condition, string failureMessage)
    {
        var stopwatch = Stopwatch.StartNew();
        while (!condition())
        {
            Assert.True(stopwatch.Elapsed < ExitTimeout, failureMessage);
            await Task.Delay(100, testContextAccessor.Current.CancellationToken);
        }
    }

    private static void KillQuietly(Process process)
    {
        try
        {
            if (!process.HasExited)
            {
                process.Kill();
            }
        }
        catch (InvalidOperationException)
        {
        }
    }
}
#endif
