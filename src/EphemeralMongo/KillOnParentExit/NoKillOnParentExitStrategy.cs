using System.Diagnostics;

namespace EphemeralMongo.KillOnParentExit;

// Used on .NET Framework, on platforms other than Windows, Linux and macOS, or when the user opted out
internal sealed class NoKillOnParentExitStrategy : IKillOnParentExitStrategy
{
    public static readonly NoKillOnParentExitStrategy Instance = new();

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
