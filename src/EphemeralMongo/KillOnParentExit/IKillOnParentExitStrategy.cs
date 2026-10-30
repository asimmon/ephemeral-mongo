using System.Diagnostics;

namespace EphemeralMongo.KillOnParentExit;

internal interface IKillOnParentExitStrategy
{
    void Configure(ProcessStartInfo processStartInfo);

    void Start(Process process, string executablePath);

    string GetTroubleshootingMessage(int exitCode, string output);
}
