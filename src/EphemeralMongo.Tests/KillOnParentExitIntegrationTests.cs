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
        Directory.CreateDirectory(dataDirectory);

        var startInfo = new ProcessStartInfo(Environment.GetEnvironmentVariable("DOTNET_HOST_PATH") ?? "dotnet")
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };

        startInfo.ArgumentList.Add(Path.Combine(AppContext.BaseDirectory, "EphemeralMongo.TestHost.dll"));
        startInfo.ArgumentList.Add(dataDirectory);
        startInfo.ArgumentList.Add(pidFilePath);

        if (useWatchdog)
        {
            startInfo.ArgumentList.Add("--watchdog");
        }

        var ready = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var host = new Process { StartInfo = startInfo };
        host.OutputDataReceived += (_, args) =>
        {
            if (args.Data == "READY")
            {
                ready.TrySetResult();
            }
        };
        host.ErrorDataReceived += (_, args) =>
        {
            if (args.Data != null)
            {
                testOutputHelper.WriteLine(args.Data);
            }
        };

        Process? mongod = null;

        try
        {
            host.Start();
            host.BeginOutputReadLine();
            host.BeginErrorReadLine();

            var cancellationToken = TestContext.Current.CancellationToken;
            await Task.WhenAny(ready.Task, host.WaitForExitAsync(cancellationToken)).WaitAsync(TimeSpan.FromMinutes(3), cancellationToken);
            Assert.True(ready.Task.IsCompleted, "The test host exited before starting mongod");

            var mongodProcessId = int.Parse(await File.ReadAllTextAsync(pidFilePath, cancellationToken), CultureInfo.InvariantCulture);
            mongod = Process.GetProcessById(mongodProcessId);

            // Not waiting for the host to exit, which also waits for its output to be closed, including by a mongod that wasn't killed
            host.Kill();

            var mongodExit = mongod.WaitForExitAsync(cancellationToken);
            await Task.WhenAny(mongodExit, Task.Delay(TimeSpan.FromSeconds(10), cancellationToken));
            Assert.True(mongodExit.IsCompleted, $"mongod {mongodProcessId} is still running after its parent process was killed");
        }
        finally
        {
            TryKill(mongod);
            TryKill(host);
            mongod?.Dispose();
            TryDeleteDirectory(directory);
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
            // Left to the operating system, as for any temporary directory
        }
    }
}
#endif
