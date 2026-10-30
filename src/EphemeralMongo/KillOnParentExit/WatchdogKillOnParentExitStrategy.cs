using System.Diagnostics;
using System.Globalization;

namespace EphemeralMongo.KillOnParentExit;

// A shell started once receives the PID of each MongoDB process on its standard input, and "-<pid>" when it exits.
// When the current process exits, even if killed, the shell reads EOF and kills the remaining ones.
// It ignores the signals sent to the whole process group, like Ctrl+C, which would otherwise kill it along with the current process.
// The log file path lets tests check what the watchdog did, see watchdog.sh
internal sealed class WatchdogKillOnParentExitStrategy(string? logFilePath = null) : IKillOnParentExitStrategy, IDisposable
{
    private const string ShellPath = "/bin/sh";

    private static readonly string WatchdogScript = ReadWatchdogScript();

    private readonly object _watchdogLock = new();
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
        var mightBeRelated = exitCode == KillOnParentExitStrategies.SigkillExitCode;

        return mightBeRelated ? " " + KillOnParentExitStrategies.DisableHint : string.Empty;
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

    // The argument after the script is its name ($0), followed by its arguments ($1, ...)
    internal static ProcessStartInfo CreateWatchdogStartInfo(string? logFilePath = null) => new()
    {
        FileName = ShellPath,
        Arguments = $"-c {ProcessArgument.Escape(WatchdogScript)} ephemeral-mongo-watchdog" + (logFilePath == null ? string.Empty : " " + ProcessArgument.Escape(logFilePath)),
        CreateNoWindow = true,
        UseShellExecute = false,
        RedirectStandardInput = true,
    };

    // Line endings are normalized in case the script was checked out with CRLF, which the shell doesn't understand
    private static string ReadWatchdogScript()
    {
        using var stream = typeof(WatchdogKillOnParentExitStrategy).Assembly.GetManifestResourceStream("EphemeralMongo.KillOnParentExit.watchdog.sh")
                           ?? throw new InvalidOperationException("The embedded watchdog script was not found");
        using var reader = new StreamReader(stream);

        return reader.ReadToEnd().Replace("\r\n", "\n");
    }

    private StreamWriter GetOrStartWatchdog()
    {
        lock (this._watchdogLock)
        {
            if (this._watchdogInput != null)
            {
                return this._watchdogInput;
            }

            var watchdog = new Process { StartInfo = CreateWatchdogStartInfo(logFilePath) };

            try
            {
                watchdog.Start();
            }
            catch (Exception ex)
            {
                watchdog.Dispose();
                throw new EphemeralMongoException($"An error occurred trying to start '{ShellPath}' to kill MongoDB processes when the current process exits. {KillOnParentExitStrategies.DisableHint}", ex);
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
