using System.Diagnostics;
using System.Runtime.InteropServices;

namespace EphemeralMongo.KillOnParentExit;

// Kills MongoDB processes when the current process exits, even if it crashes or is killed, so they don't outlive an interrupted test run:
// - .NET 11 and later on Windows and Linux: ProcessStartInfo.KillOnParentExit
// - Windows: a job object
// - Linux: setpriv, a port of the .NET 11 implementation
// - macOS, and Linux without setpriv: a watchdog shell. .NET has no equivalent on macOS, see: https://github.com/dotnet/runtime/issues/101985
internal static class KillOnParentExitStrategies
{
    // On Unix, .NET reports the exit code of a process terminated by a signal as 128 + the signal number
    internal const int SigkillExitCode = 128 + 9;

    internal const string DisableHint = "MongoDB processes are started in a way that kills them when the current process exits, which might be related to this failure. " +
        $"If so, disable this behavior with '{nameof(MongoRunnerOptions)}.{nameof(MongoRunnerOptions.KillMongoProcessesWhenCurrentProcessExits)} = false'.";

    private static readonly Lazy<IKillOnParentExitStrategy> LazyStrategy = new Lazy<IKillOnParentExitStrategy>(CreateStrategy);

    internal static IKillOnParentExitStrategy Strategy => StrategyOverride ?? LazyStrategy.Value;

    // Lets tests use the watchdog on Linux
    internal static IKillOnParentExitStrategy? StrategyOverride { get; set; }

    public static IKillOnParentExitStrategy Configure(ProcessStartInfo processStartInfo, bool enabled)
    {
        var strategy = enabled ? Strategy : NoKillOnParentExitStrategy.Instance;

        strategy.Configure(processStartInfo);

        return strategy;
    }

    private static IKillOnParentExitStrategy CreateStrategy()
    {
        // https://learn.microsoft.com/dotnet/api/system.diagnostics.processstartinfo.killonparentexit
        var net11Property = typeof(ProcessStartInfo).GetProperty("KillOnParentExit", typeof(bool));

        if (net11Property != null && (IsWindows() || IsLinux()))
        {
            return new Net11KillOnParentExitStrategy(net11Property);
        }

        if (IsWindows() && !IsNetFramework())
        {
            return new JobObjectKillOnParentExitStrategy();
        }

        if (IsLinux() && SetprivKillOnParentExitStrategy.IsAvailable())
        {
            return new SetprivKillOnParentExitStrategy();
        }

        if (IsLinux() || IsMacOS())
        {
            return new WatchdogKillOnParentExitStrategy();
        }

        return NoKillOnParentExitStrategy.Instance;
    }

    private static bool IsWindows() => RuntimeInformation.IsOSPlatform(OSPlatform.Windows);

    private static bool IsLinux() => RuntimeInformation.IsOSPlatform(OSPlatform.Linux);

    private static bool IsMacOS() => RuntimeInformation.IsOSPlatform(OSPlatform.OSX);

    // This way of detecting if running on .NET Framework is also used in .NET runtime tests, see:
    // https://github.com/dotnet/runtime/blob/v6.0.0/src/libraries/Common/tests/TestUtilities/System/PlatformDetection.Windows.cs#L21
    private static bool IsNetFramework() => RuntimeInformation.FrameworkDescription.StartsWith(".NET Framework", StringComparison.OrdinalIgnoreCase);
}
