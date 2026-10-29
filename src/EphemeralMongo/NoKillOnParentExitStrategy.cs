using System.Diagnostics;

namespace EphemeralMongo;

// Used on macOS, on .NET Framework, when setpriv isn't available on Linux, or when the user opted out
internal sealed class NoKillOnParentExitStrategy : IKillOnParentExitStrategy
{
    public static readonly NoKillOnParentExitStrategy Instance = new NoKillOnParentExitStrategy();

    private NoKillOnParentExitStrategy()
    {
    }

    public void Configure(ProcessStartInfo processStartInfo)
    {
    }

    public void Start(Process process, string executablePath)
    {
        process.Start();
    }

    public string GetTroubleshootingMessage(int exitCode, string output)
    {
        return string.Empty;
    }
}
