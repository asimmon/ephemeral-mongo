using System.Diagnostics;

namespace EphemeralMongo;

// A way to ensure MongoDB processes are killed when the current process exits, even if it crashes or is killed, see KillOnParentExit
internal interface IKillOnParentExitStrategy
{
    // Called before the process is started, can modify how the process is started
    void Configure(ProcessStartInfo processStartInfo);

    void Start(Process process, string executablePath);

    // Returns a message explaining how to disable this behavior when a process failure might be caused by it, or an empty string otherwise
    string GetTroubleshootingMessage(int exitCode, string output);
}
