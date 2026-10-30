#if NET
using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;

namespace EphemeralMongo.Tests;

public class KillOnParentExitIntegrationTests(ITestOutputHelper testOutputHelper)
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task MongoDB_Process_Is_Killed_When_Current_Process_Is_Killed(bool useWatchdog)
    {
        if (useWatchdog && RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            Assert.Skip("The watchdog is only used on Linux and macOS");
        }

        var directory = Path.Combine(Path.GetTempPath(), "ephemeral-mongo-kill-on-parent-exit-" + Path.GetRandomFileName());
        var dataDirectory = Path.Combine(directory, "data");
        var pidFilePath = Path.Combine(directory, "mongod.pid");
        var watchdogLogFilePath = Path.Combine(directory, "watchdog.log");
        Directory.CreateDirectory(dataDirectory);

        var startInfo = new ProcessStartInfo(Environment.GetEnvironmentVariable("DOTNET_HOST_PATH") ?? "dotnet")
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };

        startInfo.ArgumentList.Add(Path.Combine(AppContext.BaseDirectory, "EphemeralMongo.Tests.ParentProcess.dll"));
        startInfo.ArgumentList.Add(dataDirectory);
        startInfo.ArgumentList.Add(pidFilePath);

        if (useWatchdog)
        {
            startInfo.ArgumentList.Add("--watchdog");
            startInfo.ArgumentList.Add(watchdogLogFilePath);
        }

        var ready = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var parentProcess = new Process { StartInfo = startInfo };
        parentProcess.OutputDataReceived += (_, args) =>
        {
            if (args.Data == "READY")
            {
                ready.TrySetResult();
            }
        };
        parentProcess.ErrorDataReceived += (_, args) =>
        {
            if (args.Data != null)
            {
                testOutputHelper.WriteLine(args.Data);
            }
        };

        Process? mongod = null;

        try
        {
            parentProcess.Start();
            parentProcess.BeginOutputReadLine();
            parentProcess.BeginErrorReadLine();

            var cancellationToken = TestContext.Current.CancellationToken;
            await Task.WhenAny(ready.Task, parentProcess.WaitForExitAsync(cancellationToken)).WaitAsync(TimeSpan.FromMinutes(3), cancellationToken);
            Assert.True(ready.Task.IsCompleted, "The parent process exited before starting mongod");

            var mongodProcessId = await ReadPidFileAsync(pidFilePath, cancellationToken);
            mongod = Process.GetProcessById(mongodProcessId);

            // We don't wait for the parent process to exit after killing it. WaitForExit also waits until its redirected output is closed,
            // and a mongod that survived would keep it open, so the test would hang instead of failing.
            parentProcess.Kill();

            var mongodExit = mongod.WaitForExitAsync(cancellationToken);
            await Task.WhenAny(mongodExit, Task.Delay(TimeSpan.FromSeconds(10), cancellationToken));

            string[] watchdogLogLines = [];

            if (useWatchdog)
            {
                watchdogLogLines = await File.ReadAllLinesAsync(watchdogLogFilePath, cancellationToken);
                testOutputHelper.WriteLine(string.Join(Environment.NewLine, watchdogLogLines));
            }

            Assert.True(mongodExit.IsCompleted, $"mongod {mongodProcessId} is still running after its parent process was killed");

            // Makes sure the watchdog is what killed mongod
            if (useWatchdog)
            {
                Assert.Contains($"Killing {mongodProcessId}", watchdogLogLines);
            }
        }
        finally
        {
            TryKill(mongod);
            TryKill(parentProcess);
            mongod?.Dispose();
            TryDeleteDirectory(directory);
        }
    }

    // mongod writes its PID file after it starts accepting connections, so it might not exist yet when the parent process is ready
    private static async Task<int> ReadPidFileAsync(string pidFilePath, CancellationToken cancellationToken)
    {
        var stopwatch = Stopwatch.StartNew();

        while (true)
        {
            if (File.Exists(pidFilePath) && int.TryParse(await File.ReadAllTextAsync(pidFilePath, cancellationToken), NumberStyles.Integer, CultureInfo.InvariantCulture, out var processId))
            {
                return processId;
            }

            Assert.True(stopwatch.Elapsed < TimeSpan.FromSeconds(30), $"mongod didn't write its PID file '{pidFilePath}'");
            await Task.Delay(TimeSpan.FromMilliseconds(50), cancellationToken);
        }
    }

    private static void TryKill(Process? process)
    {
        try
        {
            process?.Kill();
            process?.WaitForExit(TimeSpan.FromSeconds(10));
        }
        catch
        {
            // The process might have already exited or might not have been started
        }
    }

    private static void TryDeleteDirectory(string directory)
    {
        try
        {
            Directory.Delete(directory, recursive: true);
        }
        catch
        {
            // Not a big deal, the operating system eventually cleans up temporary directories
        }
    }
}
#endif
