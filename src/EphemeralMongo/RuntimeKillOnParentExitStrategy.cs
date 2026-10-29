using System.Diagnostics;
using System.Reflection;
using System.Runtime.InteropServices;

namespace EphemeralMongo;

// Uses ProcessStartInfo.KillOnParentExit, introduced in .NET 11:
// - Windows: the child process is atomically assigned at creation to a job object with JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE, see: https://github.com/dotnet/runtime/pull/126699
// - Linux: the child process calls prctl(PR_SET_PDEATHSIG, SIGKILL) and is forked from a dedicated thread, see: https://github.com/dotnet/runtime/pull/127112
internal sealed class RuntimeKillOnParentExitStrategy(PropertyInfo killOnParentExitProperty) : IKillOnParentExitStrategy
{
    // On Unix, .NET reports the exit code of a process terminated by a signal as 128 + the signal number
    internal const int SigkillExitCode = 128 + 9;

    public void Configure(ProcessStartInfo processStartInfo)
    {
        killOnParentExitProperty.SetValue(processStartInfo, true);
    }

    public void Start(Process process, string executablePath)
    {
        process.Start();
    }

    public string GetTroubleshootingMessage(int exitCode, string output)
    {
        // On Linux, the MongoDB process might have been killed by the parent death signal (or by something else, like the OOM killer).
        // Users can only opt out on Linux.
        var mightBeRelated = exitCode == SigkillExitCode && RuntimeInformation.IsOSPlatform(OSPlatform.Linux);

        return mightBeRelated ? " " + KillOnParentExit.DisableHint : string.Empty;
    }
}
