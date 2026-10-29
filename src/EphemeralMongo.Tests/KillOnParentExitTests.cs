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

        KillOnParentExit.Configure(processStartInfo);

        switch (KillOnParentExit.Mode)
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

        KillOnParentExit.Configure(processStartInfo);

        using var process = new Process();
        process.StartInfo = processStartInfo;

        try
        {
            var thread = new Thread(() => KillOnParentExit.Start(process, "/bin/sh"));
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
