using System.Diagnostics;
using System.Runtime.InteropServices;

namespace EphemeralMongo;

// Ensures that MongoDB processes are killed when the current process exits, even if it crashes or is killed.
// This way, mongod processes don't keep running when developers stop a hanging test run, for instance.
//
// .NET 11 added ProcessStartInfo.KillOnParentExit, which does exactly this on Windows and Linux, so we use it whenever it is available.
// We target older frameworks, so the property is discovered at runtime. On older runtimes, we use our own implementations:
// - Windows: the current process is assigned to a job object, see JobObjectKillOnParentExitStrategy
// - Linux: a port of the .NET 11 implementation, see SetprivKillOnParentExitStrategy
// - macOS: there is no equivalent and the .NET team decided against a best-effort implementation, see: https://github.com/dotnet/runtime/issues/101985
//
// Users can opt out with MongoRunnerOptions.KillMongoProcessesWhenCurrentProcessExits, and failure messages tell them how when this might be the cause.
internal static class KillOnParentExit
{
    internal const string DisableHint = "MongoDB processes are started in a way that kills them when the current process exits, which might be related to this failure. " +
        "If so, disable this behavior with 'MongoRunnerOptions." + nameof(MongoRunnerOptions.KillMongoProcessesWhenCurrentProcessExits) + " = false'.";

    private static readonly Lazy<IKillOnParentExitStrategy> LazyStrategy = new Lazy<IKillOnParentExitStrategy>(CreateStrategy);

    // The strategy available on the current platform, regardless of the user options
    internal static IKillOnParentExitStrategy Strategy => LazyStrategy.Value;

    // Configures the process start info and returns the strategy that must be used to start the process
    public static IKillOnParentExitStrategy Configure(ProcessStartInfo processStartInfo, bool enabled)
    {
        // When disabled, the strategy isn't even created, so setpriv isn't probed and the Windows job object isn't created
        var strategy = enabled ? Strategy : NoKillOnParentExitStrategy.Instance;

        strategy.Configure(processStartInfo);

        return strategy;
    }

    private static IKillOnParentExitStrategy CreateStrategy()
    {
        var runtimeProperty = typeof(ProcessStartInfo).GetProperty("KillOnParentExit", typeof(bool));

        if (runtimeProperty != null && (IsWindows() || IsLinux()))
        {
            return new RuntimeKillOnParentExitStrategy(runtimeProperty);
        }

        // On .NET Framework, the current process crashes when assigned to the job object
        if (IsWindows() && !IsNetFramework())
        {
            return new JobObjectKillOnParentExitStrategy();
        }

        if (IsLinux() && SetprivKillOnParentExitStrategy.IsAvailable())
        {
            return new SetprivKillOnParentExitStrategy();
        }

        return NoKillOnParentExitStrategy.Instance;
    }

    private static bool IsWindows() => RuntimeInformation.IsOSPlatform(OSPlatform.Windows);

    private static bool IsLinux() => RuntimeInformation.IsOSPlatform(OSPlatform.Linux);

    // This way of detecting if running on .NET Framework is also used in .NET runtime tests, see:
    // https://github.com/dotnet/runtime/blob/v6.0.0/src/libraries/Common/tests/TestUtilities/System/PlatformDetection.Windows.cs#L21
    private static bool IsNetFramework() => RuntimeInformation.FrameworkDescription.StartsWith(".NET Framework", StringComparison.OrdinalIgnoreCase);
}
