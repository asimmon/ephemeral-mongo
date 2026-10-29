using System.Diagnostics;
using System.Runtime.InteropServices;

namespace EphemeralMongo.Tests;

public class KillOnParentExitTests
{
    private static readonly bool IsRuntimeKillOnParentExitAvailable = typeof(ProcessStartInfo).GetProperty("KillOnParentExit") != null;

    [Fact]
    public void Strategy_Depends_On_Platform_And_Runtime()
    {
        var strategy = KillOnParentExit.Strategy;

        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            if (RuntimeInformation.FrameworkDescription.StartsWith(".NET Framework", StringComparison.OrdinalIgnoreCase))
            {
                Assert.IsType<NoKillOnParentExitStrategy>(strategy);
            }
            else if (IsRuntimeKillOnParentExitAvailable)
            {
                Assert.IsType<RuntimeKillOnParentExitStrategy>(strategy);
            }
            else
            {
                Assert.IsType<JobObjectKillOnParentExitStrategy>(strategy);
            }
        }
        else if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
        {
            if (IsRuntimeKillOnParentExitAvailable)
            {
                Assert.IsType<RuntimeKillOnParentExitStrategy>(strategy);
            }
            else
            {
                // setpriv might not be available, see SetprivKillOnParentExitStrategy.IsAvailable
                Assert.True(strategy is SetprivKillOnParentExitStrategy or NoKillOnParentExitStrategy);
            }
        }
        else
        {
            Assert.IsType<NoKillOnParentExitStrategy>(strategy);
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

        var strategy = KillOnParentExit.Configure(processStartInfo, enabled: true);

        Assert.Same(KillOnParentExit.Strategy, strategy);

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

        if (strategy is RuntimeKillOnParentExitStrategy)
        {
            Assert.True((bool)typeof(ProcessStartInfo).GetProperty("KillOnParentExit")!.GetValue(processStartInfo)!);
        }
    }

    [Fact]
    public void Configure_Ignores_Disabled_Option_Except_On_Linux()
    {
        var processStartInfo = new ProcessStartInfo
        {
            FileName = "/path/to/mongod",
            Arguments = "--port 27017",
        };

        var strategy = KillOnParentExit.Configure(processStartInfo, enabled: false);

        if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
        {
            Assert.Same(NoKillOnParentExitStrategy.Instance, strategy);
            Assert.Equal("/path/to/mongod", processStartInfo.FileName);
            Assert.Equal("--port 27017", processStartInfo.Arguments);
        }
        else
        {
            Assert.Same(KillOnParentExit.Strategy, strategy);
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
    [InlineData("Runtime", 2, "sh: 1: exec: /path/to/mongod: Text file busy", false)]
    public void GetTroubleshootingMessage_Explains_How_To_Disable_The_Feature_When_It_Might_Be_Related(string strategyName, int exitCode, string output, bool expectHint)
    {
        var message = CreateStrategy(strategyName).GetTroubleshootingMessage(exitCode, output);

        AssertHint(expectHint, message);
    }

    [Fact]
    public void GetTroubleshootingMessage_Mentions_Sigkill_With_Runtime_Strategy_Only_On_Linux()
    {
        var message = CreateStrategy("Runtime").GetTroubleshootingMessage(137, string.Empty);

        AssertHint(RuntimeInformation.IsOSPlatform(OSPlatform.Linux), message);
    }

    [Fact]
    public void Process_Started_From_A_Thread_That_Exits_Is_Not_Killed()
    {
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Linux) || KillOnParentExit.Strategy is NoKillOnParentExitStrategy)
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

        var strategy = KillOnParentExit.Configure(processStartInfo, enabled: true);

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

    private static IKillOnParentExitStrategy CreateStrategy(string strategyName) => strategyName switch
    {
        "None" => NoKillOnParentExitStrategy.Instance,
        "JobObject" => new JobObjectKillOnParentExitStrategy(),
        "Setpriv" => new SetprivKillOnParentExitStrategy(),

        // The property is only used when configuring a process start info, which isn't the case here
        "Runtime" => new RuntimeKillOnParentExitStrategy(typeof(ProcessStartInfo).GetProperty(nameof(ProcessStartInfo.UseShellExecute))!),
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
