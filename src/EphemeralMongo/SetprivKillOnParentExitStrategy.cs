using System.Collections.Concurrent;
using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.Runtime.ExceptionServices;

namespace EphemeralMongo;

// A port of the Linux implementation of ProcessStartInfo.KillOnParentExit introduced in .NET 11, see:
// https://github.com/dotnet/runtime/pull/127112
//
// The child calls prctl(PR_SET_PDEATHSIG, SIGKILL), so the kernel kills it when its parent dies. However, the signal is sent
// when the *thread* that forked the child exits, not the whole process, and thread pool threads can exit at any time.
// That's why, like the .NET runtime, all processes are forked from a single dedicated thread that lives as long as the current process.
//
// The .NET runtime calls prctl in the child between fork and exec, but managed code cannot run there. Instead, we start
// "setpriv --pdeathsig KILL" (util-linux), which calls prctl and executes a shell, which in turn executes the MongoDB process.
// Each program replaces the previous one, so the PID of the started process remains the PID of the MongoDB process,
// and the parent death signal is preserved across these executions.
//
// The parent death signal is not sent if the parent died before prctl was called. Like the .NET runtime,
// the shell detects this by checking that the child process hasn't been reparented (to init or a subreaper) before executing the MongoDB process.
internal sealed class SetprivKillOnParentExitStrategy : IKillOnParentExitStrategy
{
    private const string SetprivFileName = "setpriv";
    private const string ShellPath = "/bin/sh";

    // $1 is the PID of the current process, the remaining arguments are the MongoDB executable path and its arguments
    private const string ExecIfNotReparentedScript = "[ \"$PPID\" = \"$1\" ] || exit 1; shift; exec \"$@\"";

    // https://man7.org/linux/man-pages/man3/errno.3.html
    private const int ENOENT = 2;

    private const int SetprivProbeTimeoutMs = 10_000;

    private readonly Lazy<BlockingCollection<StartRequest>> _lazyStartRequests = new Lazy<BlockingCollection<StartRequest>>(StartForkThread);

    // setpriv is part of util-linux, but --pdeathsig was only added in util-linux 2.33 (RHEL 8 ships 2.32),
    // and minimal container images like Alpine don't include setpriv at all. In that case, we do nothing, like on macOS.
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

        // setpriv --pdeathsig KILL -- /bin/sh -c '<script>' sh <current process ID> <MongoDB executable path> <MongoDB arguments>
        processStartInfo.Arguments = $"--pdeathsig KILL -- {ShellPath} -c {script} sh {currentProcessId} {executablePath} {processStartInfo.Arguments}";
        processStartInfo.FileName = SetprivFileName;
    }

    public void Start(Process process, string executablePath)
    {
        // The started executable is setpriv, so a missing MongoDB executable would only be detected later, when the shell fails to execute it.
        // The .NET runtime also checks that the executable exists before forking, so we preserve the exception thrown by Process.Start.
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
            // We already checked that the MongoDB executable exists, so setpriv itself couldn't be started
            throw new EphemeralMongoException($"An error occurred trying to start process '{executablePath}' through '{SetprivFileName}'. {KillOnParentExit.DisableHint}", request.Exception.SourceException);
        }
    }

    public string GetTroubleshootingMessage(int exitCode, string output)
    {
        // The MongoDB process was killed by the parent death signal (or by something else, like the OOM killer),
        // or setpriv or the shell failed before executing the MongoDB process, in which case they print their errors with their name as a prefix
        var mightBeRelated = exitCode == RuntimeKillOnParentExitStrategy.SigkillExitCode || HasSetprivOrShellError(output);

        return mightBeRelated ? " " + KillOnParentExit.DisableHint : string.Empty;
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

    private static BlockingCollection<StartRequest> StartForkThread()
    {
        var startRequests = new BlockingCollection<StartRequest>();

        // Never ends, so the parent death signal of the processes forked from this thread is only sent when the current process exits.
        // Background threads don't prevent the current process from exiting.
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
                // Process.Start forks from the calling thread
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

        public ManualResetEventSlim Completed { get; } = new ManualResetEventSlim(initialState: false);

        public ExceptionDispatchInfo? Exception { get; set; }
    }
}
