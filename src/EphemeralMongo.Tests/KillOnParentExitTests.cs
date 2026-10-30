using System.Diagnostics;
using System.Runtime.InteropServices;
using EphemeralMongo.KillOnParentExit;

namespace EphemeralMongo.Tests;

public class KillOnParentExitTests
{
    private static readonly bool IsNet11KillOnParentExitAvailable = typeof(ProcessStartInfo).GetProperty("KillOnParentExit") != null;

    [Fact]
    public void Strategy_Depends_On_Platform_And_Runtime()
    {
        var strategy = KillOnParentExitStrategies.Strategy;

        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            if (RuntimeInformation.FrameworkDescription.StartsWith(".NET Framework", StringComparison.OrdinalIgnoreCase))
            {
                Assert.IsType<NoKillOnParentExitStrategy>(strategy);
            }
            else if (IsNet11KillOnParentExitAvailable)
            {
                Assert.IsType<Net11KillOnParentExitStrategy>(strategy);
            }
            else
            {
                Assert.IsType<JobObjectKillOnParentExitStrategy>(strategy);
            }
        }
        else if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
        {
            if (IsNet11KillOnParentExitAvailable)
            {
                Assert.IsType<Net11KillOnParentExitStrategy>(strategy);
            }
            else
            {
                // setpriv might not be available, see SetprivKillOnParentExitStrategy.IsAvailable
                Assert.True(strategy is SetprivKillOnParentExitStrategy or WatchdogKillOnParentExitStrategy);
            }
        }
        else
        {
            Assert.IsType<WatchdogKillOnParentExitStrategy>(strategy);
        }
    }

    [Fact]
    public void Configure_Modifies_ProcessStartInfo_According_To_Strategy()
    {
        var processStartInfo = new ProcessStartInfo
        {
            FileName = "/path/to/mongod",
            Arguments = "--port 27017",
        };

        var strategy = KillOnParentExitStrategies.Configure(processStartInfo, enabled: true);

        Assert.Same(KillOnParentExitStrategies.Strategy, strategy);

        if (strategy is SetprivKillOnParentExitStrategy)
        {
            using var currentProcess = Process.GetCurrentProcess();

            Assert.Equal("setpriv", processStartInfo.FileName);
            Assert.StartsWith("--pdeathsig KILL -- /bin/sh -c ", processStartInfo.Arguments);
            Assert.EndsWith($" sh {currentProcess.Id} /path/to/mongod --port 27017", processStartInfo.Arguments);
        }
        else
        {
            Assert.Equal("/path/to/mongod", processStartInfo.FileName);
            Assert.Equal("--port 27017", processStartInfo.Arguments);
        }

        if (strategy is Net11KillOnParentExitStrategy)
        {
            Assert.True((bool)typeof(ProcessStartInfo).GetProperty("KillOnParentExit")!.GetValue(processStartInfo)!);
        }
    }

    [Fact]
    public void Configure_Does_Nothing_When_Disabled()
    {
        var processStartInfo = new ProcessStartInfo
        {
            FileName = "/path/to/mongod",
            Arguments = "--port 27017",
        };

        var strategy = KillOnParentExitStrategies.Configure(processStartInfo, enabled: false);

        Assert.Same(NoKillOnParentExitStrategy.Instance, strategy);
        Assert.Equal("/path/to/mongod", processStartInfo.FileName);
        Assert.Equal("--port 27017", processStartInfo.Arguments);

        if (typeof(ProcessStartInfo).GetProperty("KillOnParentExit") is { } property)
        {
            Assert.False((bool)property.GetValue(processStartInfo)!);
        }
    }

    [Fact]
    public void Option_Is_Enabled_By_Default_And_Copied()
    {
        Assert.True(new MongoRunnerOptions().KillMongoProcessesWhenCurrentProcessExits);

        var options = new MongoRunnerOptions { KillMongoProcessesWhenCurrentProcessExits = false };

        Assert.False(new MongoRunnerOptions(options).KillMongoProcessesWhenCurrentProcessExits);
    }

    [Theory]
    [InlineData("None", 137, "sh: 1: exec: /path/to/mongod: Text file busy", false)]
    [InlineData("JobObject", 137, "sh: 1: exec: /path/to/mongod: Text file busy", false)]
    [InlineData("Setpriv", 2, "sh: 1: exec: /path/to/mongod: Text file busy", true)]
    [InlineData("Setpriv", 1, "setpriv: setresuid failed: Operation not permitted", true)]
    [InlineData("Setpriv", 137, "", true)]
    [InlineData("Setpriv", 1, "{\"t\":{\"$date\":\"2026-10-29T00:00:00.000Z\"},\"s\":\"E\",\"msg\":\"Address already in use\"}", false)]
    [InlineData("Net11", 2, "sh: 1: exec: /path/to/mongod: Text file busy", false)]
    [InlineData("Watchdog", 137, "", true)]
    [InlineData("Watchdog", 2, "sh: 1: exec: /path/to/mongod: Text file busy", false)]
    [InlineData("Watchdog", 0, "", false)]
    public void GetTroubleshootingMessage_Explains_How_To_Disable_The_Feature_When_It_Might_Be_Related(string strategyName, int exitCode, string output, bool expectHint)
    {
        var message = CreateStrategy(strategyName).GetTroubleshootingMessage(exitCode, output);

        AssertHint(expectHint, message);
    }

    [Fact]
    public void GetTroubleshootingMessage_Mentions_Sigkill_With_Net11_Strategy_Only_On_Linux()
    {
        var message = CreateStrategy("Net11").GetTroubleshootingMessage(137, string.Empty);

        AssertHint(RuntimeInformation.IsOSPlatform(OSPlatform.Linux), message);
    }

    [Fact]
    public void Process_Started_From_A_Thread_That_Exits_Is_Not_Killed()
    {
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Linux) || KillOnParentExitStrategies.Strategy is NoKillOnParentExitStrategy)
        {
            Assert.Skip("This test is specific to Linux, where the parent death signal is sent when the thread that forked the child exits");
        }

        // Processes must be forked from a dedicated thread, otherwise they would be killed when this thread exits
        var processStartInfo = new ProcessStartInfo
        {
            FileName = "/bin/sh",
            Arguments = "-c \"exec sleep 30\"",
            UseShellExecute = false,
        };

        var strategy = KillOnParentExitStrategies.Configure(processStartInfo, enabled: true);

        using var process = new Process();
        process.StartInfo = processStartInfo;

        try
        {
            var thread = new Thread(() => strategy.Start(process, "/bin/sh"));
            thread.Start();
            thread.Join();

            Assert.False(process.WaitForExit(milliseconds: 1000));
        }
        finally
        {
            try
            {
                process.Kill();
            }
            catch
            {
                // The process might have already exited
            }
        }
    }

    [Fact]
    public void Watchdog_Kills_Started_Processes_When_Current_Process_Exits()
    {
        SkipOnWindows();

        using var exitedProcess = CreateShellProcess("exit 0");
        using var sleepingProcess1 = CreateShellProcess("exec sleep 30");
        using var sleepingProcess2 = CreateShellProcess("exec sleep 30");
        using var unrelatedProcess = CreateShellProcess("exec sleep 30");

        try
        {
            using (var strategy = new WatchdogKillOnParentExitStrategy())
            {
                strategy.Start(exitedProcess, "/bin/sh");
                Assert.True(exitedProcess.WaitForExit(milliseconds: 5000));

                strategy.Start(sleepingProcess1, "/bin/sh");
                strategy.Start(sleepingProcess2, "/bin/sh");
                unrelatedProcess.Start();

                // Nothing is killed while the current process is alive
                Assert.False(sleepingProcess1.WaitForExit(milliseconds: 500));

                // Disposing the strategy closes the standard input of the watchdog, like the operating system does when the current process exits
            }

            AssertKilled(sleepingProcess1);
            AssertKilled(sleepingProcess2);
            Assert.False(unrelatedProcess.HasExited);
            Assert.Equal(0, exitedProcess.ExitCode);
        }
        finally
        {
            TryKill(sleepingProcess1, sleepingProcess2, unrelatedProcess);
        }
    }

    [Fact]
    public void Watchdog_Forgets_Processes_Prefixed_With_A_Dash_And_Ignores_Invalid_Lines()
    {
        SkipOnWindows();

        using var forgottenProcess = CreateShellProcess("exec sleep 30");
        using var killedProcess = CreateShellProcess("exec sleep 30");
        using var exitedProcess = CreateShellProcess("exit 0");

        try
        {
            forgottenProcess.Start();
            killedProcess.Start();
            exitedProcess.Start();
            exitedProcess.WaitForExit();

            RunWatchdog(
                $"{forgottenProcess.Id}",
                $"{killedProcess.Id}",
                $"{killedProcess.Id}",
                $"-{forgottenProcess.Id}",
                $"{exitedProcess.Id}",
                string.Empty,
                "-",
                "--1",
                "0",
                "abc",
                $"{killedProcess.Id}abc",
                "$(exit 1)",
                $"-{killedProcess.Id}x");

            AssertKilled(killedProcess);
            Assert.False(forgottenProcess.HasExited);
        }
        finally
        {
            TryKill(forgottenProcess, killedProcess);
        }
    }

    [Theory]
    [InlineData("INT")]
    [InlineData("TERM")]
    [InlineData("HUP")]
    [InlineData("QUIT")]
    public void Watchdog_Ignores_Termination_Signals(string signal)
    {
        SkipOnWindows();

        using var sleepingProcess = CreateShellProcess("exec sleep 30");

        try
        {
            sleepingProcess.Start();

            using var watchdog = Process.Start(WatchdogKillOnParentExitStrategy.CreateWatchdogStartInfo())!;
            watchdog.StandardInput.Write($"{sleepingProcess.Id}\n");
            watchdog.StandardInput.Flush();

            // Pressing Ctrl+C in a terminal sends SIGINT to the whole process group, including the watchdog
            using (var kill = Process.Start("kill", $"-{signal} {watchdog.Id}")!)
            {
                kill.WaitForExit();
                Assert.Equal(0, kill.ExitCode);
            }

            Assert.False(watchdog.WaitForExit(milliseconds: 500));

            watchdog.StandardInput.Close();
            Assert.True(watchdog.WaitForExit(milliseconds: 5000));

            AssertKilled(sleepingProcess);
        }
        finally
        {
            TryKill(sleepingProcess);
        }
    }

    private static void SkipOnWindows()
    {
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            Assert.Skip("The watchdog is only used on Linux and macOS");
        }
    }

    private static Process CreateShellProcess(string script) => new Process
    {
        StartInfo = new ProcessStartInfo
        {
            FileName = "/bin/sh",
            Arguments = $"-c \"{script}\"",
            UseShellExecute = false,
        },
    };

    // Writes the lines to a new watchdog, then closes its standard input and waits for it to exit
    private static void RunWatchdog(params string[] lines)
    {
        using var watchdog = Process.Start(WatchdogKillOnParentExitStrategy.CreateWatchdogStartInfo())!;

        foreach (var line in lines)
        {
            watchdog.StandardInput.Write(line + "\n");
        }

        watchdog.StandardInput.Close();

        Assert.True(watchdog.WaitForExit(milliseconds: 5000));
        Assert.Equal(0, watchdog.ExitCode);
    }

    private static void AssertKilled(Process process)
    {
        Assert.True(process.WaitForExit(milliseconds: 5000));
        Assert.Equal(KillOnParentExitStrategies.SigkillExitCode, process.ExitCode);
    }

    private static void TryKill(params Process[] processes)
    {
        foreach (var process in processes)
        {
            try
            {
                process.Kill();
            }
            catch
            {
                // The process might have already exited or might not have been started
            }
        }
    }

    private static IKillOnParentExitStrategy CreateStrategy(string strategyName) => strategyName switch
    {
        "None" => NoKillOnParentExitStrategy.Instance,
        "JobObject" => new JobObjectKillOnParentExitStrategy(),
        "Setpriv" => new SetprivKillOnParentExitStrategy(),
        "Watchdog" => new WatchdogKillOnParentExitStrategy(),

        // The property is only used when configuring a process start info, which isn't the case here
        "Net11" => new Net11KillOnParentExitStrategy(typeof(ProcessStartInfo).GetProperty(nameof(ProcessStartInfo.UseShellExecute))!),
        _ => throw new ArgumentOutOfRangeException(nameof(strategyName)),
    };

    private static void AssertHint(bool expectHint, string message)
    {
        if (expectHint)
        {
            Assert.Contains("MongoRunnerOptions.KillMongoProcessesWhenCurrentProcessExits = false", message);
        }
        else
        {
            Assert.Empty(message);
        }
    }
}
