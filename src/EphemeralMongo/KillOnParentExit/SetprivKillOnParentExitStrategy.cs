using System.Collections.Concurrent;
using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.Runtime.ExceptionServices;

namespace EphemeralMongo.KillOnParentExit;

// A port of the Linux implementation of ProcessStartInfo.KillOnParentExit from .NET 11, see: https://github.com/dotnet/runtime/pull/127112
// Managed code can't call prctl(PR_SET_PDEATHSIG) between fork and exec, so MongoDB processes are executed through "setpriv --pdeathsig KILL".
internal sealed class SetprivKillOnParentExitStrategy : IKillOnParentExitStrategy
{
    private const string SetprivFileName = "setpriv";
    private const string ShellPath = "/bin/sh";

    // Like .NET, detects that the current process died before the parent death signal was set. $1 is the PID of the current process.
    private const string ExecIfNotReparentedScript = "[ \"$PPID\" = \"$1\" ] || exit 1; shift; exec \"$@\"";

    // https://man7.org/linux/man-pages/man3/errno.3.html
    private const int ENOENT = 2;

    private const int SetprivProbeTimeoutMs = 10_000;

    private readonly Lazy<BlockingCollection<StartRequest>> _lazyStartRequests = new(StartForkThread);

    // --pdeathsig requires util-linux 2.33 (RHEL 8 ships 2.32), and minimal images like Alpine don't include setpriv
    public static bool IsAvailable()
    {
        try
        {
            var processStartInfo = new ProcessStartInfo
            {
                FileName = SetprivFileName,
                Arguments = $"--pdeathsig KILL -- {ShellPath} -c \"exit 0\"",
                CreateNoWindow = true,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            };

            using var process = Process.Start(processStartInfo);

            if (process == null)
            {
                return false;
            }

            if (!process.WaitForExit(SetprivProbeTimeoutMs))
            {
                process.Kill();
                return false;
            }

            return process.ExitCode == 0;
        }
        catch
        {
            return false;
        }
    }

    public void Configure(ProcessStartInfo processStartInfo)
    {
        var currentProcessId = GetCurrentProcessId().ToString(CultureInfo.InvariantCulture);
        var script = ProcessArgument.Escape(ExecIfNotReparentedScript);
        var executablePath = ProcessArgument.Escape(processStartInfo.FileName);

        processStartInfo.Arguments = $"--pdeathsig KILL -- {ShellPath} -c {script} sh {currentProcessId} {executablePath} {processStartInfo.Arguments}";
        processStartInfo.FileName = SetprivFileName;
    }

    public void Start(Process process, string executablePath)
    {
        // Like Process.Start, throw for a missing executable, which the shell would otherwise only report once started
        if (!File.Exists(executablePath))
        {
            throw new Win32Exception(ENOENT, $"An error occurred trying to start process '{executablePath}' with working directory '{Environment.CurrentDirectory}'. No such file or directory");
        }

        var request = new StartRequest(process);

        this._lazyStartRequests.Value.Add(request);

        request.Completed.Wait();
        request.Completed.Dispose();

        if (request.Exception != null)
        {
            throw new EphemeralMongoException($"An error occurred trying to start process '{executablePath}' through '{SetprivFileName}'. {KillOnParentExitStrategies.DisableHint}", request.Exception.SourceException);
        }
    }

    public string GetTroubleshootingMessage(int exitCode, string output)
    {
        var mightBeRelated = exitCode == KillOnParentExitStrategies.SigkillExitCode || HasSetprivOrShellError(output);

        return mightBeRelated ? " " + KillOnParentExitStrategies.DisableHint : string.Empty;
    }

    private static bool HasSetprivOrShellError(string output)
    {
        using var reader = new StringReader(output);

        while (reader.ReadLine() is { } line)
        {
            line = line.TrimStart();

            if (line.StartsWith(SetprivFileName + ":", StringComparison.Ordinal) || line.StartsWith("sh:", StringComparison.Ordinal))
            {
                return true;
            }
        }

        return false;
    }

    private static int GetCurrentProcessId()
    {
        using var currentProcess = Process.GetCurrentProcess();
        return currentProcess.Id;
    }

    // On Linux, the parent death signal is sent when the thread that started the child process exits, not when the whole process exits.
    // If MongoDB were started from an arbitrary thread, such as a thread pool thread that later exits, MongoDB would be killed too early.
    // That's why every process is started from this dedicated thread, which lives as long as the current process.
    // A Task wouldn't be a reliable replacement: there's no guarantee about which thread runs it, and that thread could exit.
    private static BlockingCollection<StartRequest> StartForkThread()
    {
        var startRequests = new BlockingCollection<StartRequest>();

        var thread = new Thread(() => ProcessStartRequests(startRequests))
        {
            IsBackground = true,
            Name = "EphemeralMongo process starter",
        };

        thread.Start();

        return startRequests;
    }

    private static void ProcessStartRequests(BlockingCollection<StartRequest> startRequests)
    {
        foreach (var request in startRequests.GetConsumingEnumerable())
        {
            try
            {
                request.Process.Start();
            }
            catch (Exception ex)
            {
                request.Exception = ExceptionDispatchInfo.Capture(ex);
            }
            finally
            {
                request.Completed.Set();
            }
        }
    }

    private sealed class StartRequest(Process process)
    {
        public Process Process { get; } = process;

        public ManualResetEventSlim Completed { get; } = new(initialState: false);

        public ExceptionDispatchInfo? Exception { get; set; }
    }
}
