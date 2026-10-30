using System.Diagnostics;
using System.Reflection;
using System.Runtime.InteropServices;

namespace EphemeralMongo;

internal sealed class Net11KillOnParentExitStrategy(PropertyInfo killOnParentExitProperty) : IKillOnParentExitStrategy
{
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
        // On Linux, the parent death signal is SIGKILL
        var mightBeRelated = exitCode == KillOnParentExit.SigkillExitCode && RuntimeInformation.IsOSPlatform(OSPlatform.Linux);

        return mightBeRelated ? " " + KillOnParentExit.DisableHint : string.Empty;
    }
}
