using System.Diagnostics;
using System.Reflection;
using System.Runtime.InteropServices;

namespace EphemeralMongo.KillOnParentExit;

internal sealed class Net11KillOnParentExitStrategy : IKillOnParentExitStrategy
{
    // https://learn.microsoft.com/dotnet/api/system.diagnostics.processstartinfo.killonparentexit
    private static readonly PropertyInfo? KillOnParentExitProperty = typeof(ProcessStartInfo).GetProperty("KillOnParentExit", typeof(bool));

    public static bool IsAvailable() => KillOnParentExitProperty != null;

    public void Configure(ProcessStartInfo processStartInfo)
    {
        KillOnParentExitProperty!.SetValue(processStartInfo, true);
    }

    public void Start(Process process, string executablePath)
    {
        process.Start();
    }

    public string GetTroubleshootingMessage(int exitCode, string output)
    {
        // On Linux, the parent death signal is SIGKILL
        var mightBeRelated = exitCode == KillOnParentExitStrategies.SigkillExitCode && RuntimeInformation.IsOSPlatform(OSPlatform.Linux);

        return mightBeRelated ? " " + KillOnParentExitStrategies.DisableHint : string.Empty;
    }
}
