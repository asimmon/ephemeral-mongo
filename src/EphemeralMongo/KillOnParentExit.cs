using System.Diagnostics;
using System.Reflection;
using System.Runtime.InteropServices;

namespace EphemeralMongo;

internal static class KillOnParentExit
{
    // .NET 11 added ProcessStartInfo.KillOnParentExit. On Linux, the runtime forks the child from a dedicated long-lived thread
    // and calls prctl(PR_SET_PDEATHSIG, SIGKILL) in the child, so the kernel kills the child when the current process exits, even if it crashes or is killed.
    // A dedicated thread is required because the signal is sent when the thread that forked the child exits, not the whole process,
    // and thread pool threads can exit at any time. We target older frameworks, so the property is discovered at runtime.
    // See: https://github.com/dotnet/runtime/pull/127112
    //
    // On Windows, we already rely on a job object that contains the current process and all its children, see NativeMethods.
    // On macOS, there is no equivalent and the .NET team decided against a best-effort implementation, see: https://github.com/dotnet/runtime/issues/101985
    private static readonly PropertyInfo? KillOnParentExitProperty = typeof(ProcessStartInfo).GetProperty("KillOnParentExit", typeof(bool));

    public static bool IsSupported => KillOnParentExitProperty != null && RuntimeInformation.IsOSPlatform(OSPlatform.Linux);

    public static void EnableIfSupported(ProcessStartInfo processStartInfo)
    {
        if (IsSupported)
        {
            KillOnParentExitProperty!.SetValue(processStartInfo, true);
        }
    }
}
