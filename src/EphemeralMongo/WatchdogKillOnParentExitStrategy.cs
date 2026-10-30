using System.Diagnostics;
using System.Globalization;

namespace EphemeralMongo;

// A shell started once receives the PID of each MongoDB process on its standard input, and "-<pid>" when it exits.
// When the current process exits, even if killed, the shell reads EOF and kills the remaining ones.
// It ignores the signals sent to the whole process group, like Ctrl+C, which would otherwise kill it along with the current process.
internal sealed class WatchdogKillOnParentExitStrategy : IKillOnParentExitStrategy, IDisposable
{
    private const string ShellPath = "/bin/sh";

    // Start times guard against PID reuse. On Linux, it's the 22nd field of /proc/<pid>/stat, after the command name which can contain spaces.
    // PIDs starting with 0 are ignored, as "kill -9 0" kills the whole process group.
    private const string WatchdogScript =
        "trap '' HUP INT QUIT TERM\n" +
        "exec >/dev/null 2>&1\n" +
        "read_start_time() {\n" +
        "  start_time=\n" +
        "  if [ -d /proc/self ]; then\n" +
        "    read -r stat < \"/proc/$1/stat\" || return 1\n" +
        "    set -- ${stat##*\") \"}\n" +
        "    [ $# -ge 20 ] || return 1\n" +
        "    shift 19\n" +
        "    start_time=$1\n" +
        "  else\n" +
        "    start_time=$(ps -o lstart= -p \"$1\")\n" +
        "  fi\n" +
        "  [ -n \"$start_time\" ]\n" +
        "}\n" +
        "pids=\n" +
        "while read -r line; do\n" +
        "  pid=${line#-}\n" +
        "  case $pid in ''|0*|*[!0-9]*) continue ;; esac\n" +
        "  remaining=\n" +
        "  for p in $pids; do [ \"$p\" = \"$pid\" ] || remaining=\"$remaining $p\"; done\n" +
        "  pids=$remaining\n" +
        "  if [ \"$pid\" = \"$line\" ] && read_start_time \"$pid\"; then\n" +
        "    pids=\"$pids $pid\"\n" +
        "    eval \"start_time_$pid=\\$start_time\"\n" +
        "  fi\n" +
        "done\n" +
        "for pid in $pids; do\n" +
        "  eval \"expected_start_time=\\$start_time_$pid\"\n" +
        "  read_start_time \"$pid\" && [ \"$start_time\" = \"$expected_start_time\" ] && kill -9 \"$pid\"\n" +
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

        var processId = process.Id.ToString(CultureInfo.InvariantCulture);
        process.EnableRaisingEvents = true;
        process.Exited += (_, _) => this.WriteLine(watchdogInput, "-" + processId);

        this.WriteLine(watchdogInput, processId);

        // In case it exited before we subscribed to the Exited event
        if (process.HasExited)
        {
            this.WriteLine(watchdogInput, "-" + processId);
        }
    }

    public string GetTroubleshootingMessage(int exitCode, string output)
    {
        var mightBeRelated = exitCode == KillOnParentExit.SigkillExitCode;

        return mightBeRelated ? " " + KillOnParentExit.DisableHint : string.Empty;
    }

    // Only used in tests, as the watchdog must live as long as the current process
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

    internal static ProcessStartInfo CreateWatchdogStartInfo() => new ProcessStartInfo
    {
        FileName = ShellPath,
        Arguments = $"-c {ProcessArgument.Escape(WatchdogScript)} ephemeral-mongo-watchdog",
        CreateNoWindow = true,
        UseShellExecute = false,
        RedirectStandardInput = true,
    };

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
                // The watchdog was killed, there's nothing more we can do
            }
        }
    }
}
