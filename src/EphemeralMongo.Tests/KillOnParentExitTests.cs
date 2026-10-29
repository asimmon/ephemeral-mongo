using System.Diagnostics;
using System.Runtime.InteropServices;

namespace EphemeralMongo.Tests;

public class KillOnParentExitTests
{
    [Fact]
    public void Mode_Is_None_On_Other_Platforms_Than_Linux()
    {
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
        {
            Assert.Skip("KillOnParentExit is supported on Linux");
        }

        Assert.Equal(KillOnParentExitMode.None, KillOnParentExit.Mode);
    }

    [Fact]
    public void Configure_Modifies_ProcessStartInfo_According_To_Mode()
    {
        var processStartInfo = new ProcessStartInfo
        {
            FileName = "/path/to/mongod",
            Arguments = "--port 27017",
        };

        var mode = KillOnParentExit.Configure(processStartInfo, enabled: true);

        Assert.Equal(KillOnParentExit.Mode, mode);

        switch (mode)
        {
            case KillOnParentExitMode.None:
                Assert.Equal("/path/to/mongod", processStartInfo.FileName);
                Assert.Equal("--port 27017", processStartInfo.Arguments);
                break;

            case KillOnParentExitMode.Runtime:
                Assert.Equal("/path/to/mongod", processStartInfo.FileName);
                Assert.Equal("--port 27017", processStartInfo.Arguments);
                Assert.True((bool)typeof(ProcessStartInfo).GetProperty("KillOnParentExit")!.GetValue(processStartInfo)!);
                break;

            case KillOnParentExitMode.Setpriv:
                using (var currentProcess = Process.GetCurrentProcess())
                {
                    Assert.Equal("setpriv", processStartInfo.FileName);
                    Assert.StartsWith("--pdeathsig KILL -- /bin/sh -c ", processStartInfo.Arguments);
                    Assert.EndsWith($" sh {currentProcess.Id} /path/to/mongod --port 27017", processStartInfo.Arguments);
                }

                break;
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

        var mode = KillOnParentExit.Configure(processStartInfo, enabled: false);

        Assert.Equal(KillOnParentExitMode.None, mode);
        Assert.Equal("/path/to/mongod", processStartInfo.FileName);
        Assert.Equal("--port 27017", processStartInfo.Arguments);
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
    [InlineData("Setpriv", 2, "sh: 1: exec: /path/to/mongod: Text file busy", true)]
    [InlineData("Setpriv", 1, "setpriv: setresuid failed: Operation not permitted", true)]
    [InlineData("Setpriv", 137, "", true)]
    [InlineData("Setpriv", 1, "{\"t\":{\"$date\":\"2026-10-29T00:00:00.000Z\"},\"s\":\"E\",\"msg\":\"Address already in use\"}", false)]
    [InlineData("Runtime", 137, "", true)]
    [InlineData("Runtime", 2, "sh: 1: exec: /path/to/mongod: Text file busy", false)]
    public void GetTroubleshootingMessage_Explains_How_To_Disable_The_Feature_When_It_Might_Be_Related(string modeName, int exitCode, string output, bool expectHint)
    {
        var mode = (KillOnParentExitMode)Enum.Parse(typeof(KillOnParentExitMode), modeName);
        var message = KillOnParentExit.GetTroubleshootingMessage(mode, exitCode, output);

        if (expectHint)
        {
            Assert.Contains("MongoRunnerOptions.KillMongoProcessesWhenCurrentProcessExits = false", message);
        }
        else
        {
            Assert.Empty(message);
        }
    }

    [Fact]
    public void Process_Started_From_A_Thread_That_Exits_Is_Not_Killed()
    {
        if (KillOnParentExit.Mode == KillOnParentExitMode.None)
        {
            Assert.Skip("KillOnParentExit is not supported on this platform");
        }

        // The parent death signal is sent when the thread that forked the child exits, unless processes are forked from a dedicated thread
        var processStartInfo = new ProcessStartInfo
        {
            FileName = "/bin/sh",
            Arguments = "-c \"exec sleep 30\"",
            UseShellExecute = false,
        };

        var mode = KillOnParentExit.Configure(processStartInfo, enabled: true);

        using var process = new Process();
        process.StartInfo = processStartInfo;

        try
        {
            var thread = new Thread(() => KillOnParentExit.Start(process, "/bin/sh", mode));
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
}
