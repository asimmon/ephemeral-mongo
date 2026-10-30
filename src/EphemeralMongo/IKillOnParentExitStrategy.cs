using System.Diagnostics;

namespace EphemeralMongo;

internal interface IKillOnParentExitStrategy
{
    void Configure(ProcessStartInfo processStartInfo);

    void Start(Process process, string executablePath);

    string GetTroubleshootingMessage(int exitCode, string output);
}
