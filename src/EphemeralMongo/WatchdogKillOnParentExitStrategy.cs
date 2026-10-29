using System.Diagnostics;
using System.Globalization;

namespace EphemeralMongo;

// Used on macOS, and on Linux before .NET 11 when setpriv isn't available. Neither job objects nor prctl(PR_SET_PDEATHSIG) exist on macOS.
//
// A single long-lived shell, the watchdog, is started with a redirected standard input, and the PID of each started MongoDB process is written to it.
// When the current process exits for any reason, including SIGKILL, the kernel closes its end of the pipe, so the watchdog reads EOF
// and kills the MongoDB processes it still knows about. MongoDB processes are started as usual and remain direct children of the current process.
//
// When a MongoDB process exits, its PID is written again with a "-" prefix so the watchdog forgets it. Because PIDs are reused, the watchdog
// also records the start time of each process and only kills processes whose start time hasn't changed. This also covers processes whose exit
// couldn't be reported before the current process died.
//
// The watchdog ignores SIGHUP, SIGINT, SIGQUIT and SIGTERM. It belongs to the same process group as the current process, so pressing Ctrl+C
// in a terminal would otherwise kill it along with the current process, and the MongoDB processes would keep running.
//
// A MongoDB process isn't killed if the current process dies after starting it but before writing its PID to the watchdog.
internal sealed class WatchdogKillOnParentExitStrategy : IKillOnParentExitStrategy, IDisposable
{
    private const string ShellPath = "/bin/sh";

    // "identify" sets $ident to the start time of the process $1, from /proc/<pid>/stat on Linux (the 22nd field, the 20th after the command name
    // which can contain spaces and parentheses) or from ps on macOS. Unknown PIDs and malformed lines are ignored, and nothing is printed.
    // PIDs starting with 0 are rejected, as "kill -9 0" would kill the whole process group.
    private const string WatchdogScript =
        "trap '' HUP INT QUIT TERM\n" +
        "exec >/dev/null 2>&1\n" +
        "identify() {\n" +
        "  ident=\n" +
        "  if [ -d /proc/self ]; then\n" +
        "    read -r stat < \"/proc/$1/stat\" || return 1\n" +
        "    set -- ${stat##*\") \"}\n" +
        "    [ $# -ge 20 ] || return 1\n" +
        "    shift 19\n" +
        "    ident=$1\n" +
        "  else\n" +
        "    ident=$(ps -o lstart= -p \"$1\")\n" +
        "  fi\n" +
        "  [ -n \"$ident\" ]\n" +
        "}\n" +
        "pids=\n" +
        "while read -r line; do\n" +
        "  pid=${line#-}\n" +
        "  case $pid in ''|0*|*[!0-9]*) continue ;; esac\n" +
        "  remaining=\n" +
        "  for p in $pids; do [ \"$p\" = \"$pid\" ] || remaining=\"$remaining $p\"; done\n" +
        "  pids=$remaining\n" +
        "  if [ \"$pid\" = \"$line\" ] && identify \"$pid\"; then\n" +
        "    pids=\"$pids $pid\"\n" +
        "    eval \"ident_$pid=\\$ident\"\n" +
        "  fi\n" +
        "done\n" +
        "for pid in $pids; do\n" +
        "  eval \"expected=\\$ident_$pid\"\n" +
        "  identify \"$pid\" && [ \"$ident\" = \"$expected\" ] && kill -9 \"$pid\"\n" +
        "done\n";

    private readonly object _watchdogLock = new object();
    private Process? _watchdog;
    private StreamWriter? _watchdogInput;

    public void Configure(ProcessStartInfo processStartInfo)
    {
    }

    public void Start(Process process, string executablePath)
    {
        var watchdogInput = this.GetOrStartWatchdog();

        process.Start();

        // The process might exit before we subscribe to the Exited event, which is why we also check HasExited after writing its PID.
        // Forgetting a process twice is harmless.
        var processId = process.Id.ToString(CultureInfo.InvariantCulture);
        process.EnableRaisingEvents = true;
        process.Exited += (_, _) => this.WriteLine(watchdogInput, "-" + processId);

        this.WriteLine(watchdogInput, processId);

        if (process.HasExited)
        {
            this.WriteLine(watchdogInput, "-" + processId);
        }
    }

    public string GetTroubleshootingMessage(int exitCode, string output)
    {
        // The MongoDB process might have been killed by the watchdog (or by something else, like the OOM killer)
        var mightBeRelated = exitCode == RuntimeKillOnParentExitStrategy.SigkillExitCode;

        return mightBeRelated ? " " + KillOnParentExit.DisableHint : string.Empty;
    }

    // Only used in tests, as the watchdog must live as long as the current process.
    // Closing its standard input has the same effect as the current process exiting.
    public void Dispose()
    {
        lock (this._watchdogLock)
        {
            if (this._watchdog != null)
            {
                this._watchdogInput!.Dispose();
                this._watchdog.WaitForExit();
                this._watchdog.Dispose();
                this._watchdog = null;
                this._watchdogInput = null;
            }
        }
    }

    private StreamWriter GetOrStartWatchdog()
    {
        lock (this._watchdogLock)
        {
            if (this._watchdogInput != null)
            {
                return this._watchdogInput;
            }

            var watchdog = new Process { StartInfo = CreateWatchdogStartInfo() };

            try
            {
                watchdog.Start();
            }
            catch (Exception ex)
            {
                watchdog.Dispose();
                throw new EphemeralMongoException($"An error occurred trying to start '{ShellPath}' to kill MongoDB processes when the current process exits. {KillOnParentExit.DisableHint}", ex);
            }

            this._watchdog = watchdog;
            this._watchdogInput = watchdog.StandardInput;

            return this._watchdogInput;
        }
    }

    // Internal for tests, which write to the watchdog directly
    internal static ProcessStartInfo CreateWatchdogStartInfo() => new ProcessStartInfo
    {
        FileName = ShellPath,
        Arguments = $"-c {ProcessArgument.Escape(WatchdogScript)} ephemeral-mongo-watchdog",
        CreateNoWindow = true,
        UseShellExecute = false,
        RedirectStandardInput = true,
    };

    private void WriteLine(StreamWriter watchdogInput, string line)
    {
        lock (this._watchdogLock)
        {
            try
            {
                watchdogInput.Write(line + "\n");
                watchdogInput.Flush();
            }
            catch (Exception ex) when (ex is IOException or ObjectDisposedException)
            {
                // The watchdog was killed by someone else (or disposed in tests), there's nothing more we can do
            }
        }
    }
}
