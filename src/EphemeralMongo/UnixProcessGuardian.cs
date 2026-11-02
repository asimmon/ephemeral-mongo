using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;

namespace EphemeralMongo;

/// <summary>
/// Kills a mongod process when the current process dies without disposing it, on Linux and macOS.
/// </summary>
/// <remarks>
/// On Windows, <see cref="NativeMethods"/> assigns the current process to a job object so that child processes
/// die with it. Unix has no equivalent that survives SIGKILL and can be set up through <see cref="Process"/>,
/// so a killed test host used to leave mongod running, reparented to PID 1, with its data directory.
///
/// The guardian is a small /bin/sh process whose standard input is a pipe held open by the current process.
/// It blocks reading that pipe. When the current process exits for any reason, including SIGKILL, the kernel
/// closes the write end and the read returns end-of-file; the guardian then kills the mongod process and
/// removes its data directory. On a normal dispose, the current process writes "stop" first and the guardian
/// exits without touching anything.
/// </remarks>
internal sealed class UnixProcessGuardian : IDisposable
{
    internal const string StopCommand = "stop";

    // $1: pid to kill, $2: directory to remove (may be empty), $3: text the process command line must contain.
    // The command line check guards against killing an unrelated process that reused the pid after mongod
    // exited on its own; it is skipped where ps is unavailable.
    private const string Script =
        "exec >/dev/null 2>&1\n" +
        "trap '' INT HUP\n" +
        "IFS= read -r line\n" +
        "[ \"$line\" = " + StopCommand + " ] && exit 0\n" +
        "if command -v ps >/dev/null; then ps -o args= -p \"$1\" | grep -qF -- \"$3\" || exit 0; fi\n" +
        "kill -9 \"$1\"\n" +
        "[ -n \"$2\" ] && rm -rf \"$2\"\n" +
        "exit 0\n";

    private readonly Process _process;
    private int _isInputClosed;
    private int _isDisposed;

    private UnixProcessGuardian(Process process)
    {
        this._process = process;
    }

    internal int ProcessId => this._process.Id;

    public static bool IsSupported => (RuntimeInformation.IsOSPlatform(OSPlatform.Linux) || RuntimeInformation.IsOSPlatform(OSPlatform.OSX)) && !NativeMethods.IsNetFramework();

    /// <summary>
    /// Starts a guardian for <paramref name="targetProcessId"/>. Returns null when unsupported or when the guardian cannot start,
    /// in which case the target simply runs unguarded, as before.
    /// </summary>
    public static UnixProcessGuardian? TryStart(int targetProcessId, string? directoryToDelete, string commandLineMarker, Logger? errorLogger)
    {
        if (!IsSupported)
        {
            return null;
        }

        var arguments = string.Join(" ", new[]
        {
            "-c",
            ProcessArgument.Escape(Script),
            "ephemeral-mongo-guardian",
            targetProcessId.ToString(CultureInfo.InvariantCulture),
            ProcessArgument.Escape(directoryToDelete ?? string.Empty),
            ProcessArgument.Escape(commandLineMarker),
        });

        var process = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = "/bin/sh",
                Arguments = arguments,
                CreateNoWindow = true,
                UseShellExecute = false,
                RedirectStandardInput = true,
            },
        };

        try
        {
            process.Start();
            return new UnixProcessGuardian(process);
        }
        catch (Exception ex) when (ex is Win32Exception or InvalidOperationException)
        {
            errorLogger?.Invoke($"Could not start the process guardian, mongod will not be stopped if this process is killed: {ex.Message}");
            process.Dispose();
            return null;
        }
    }

    /// <summary>
    /// Closes the guardian's input without sending the stop command, exactly as the kernel does when the current process dies.
    /// </summary>
    internal void SimulateOwnerExit()
    {
        if (Interlocked.CompareExchange(ref this._isInputClosed, 1, 0) == 0)
        {
            TryClose(this._process.StandardInput, sendStop: false);
        }
    }

    internal bool WaitForExit(TimeSpan timeout) => this._process.WaitForExit((int)timeout.TotalMilliseconds);

    public void Dispose()
    {
        if (Interlocked.CompareExchange(ref this._isDisposed, 1, 0) != 0)
        {
            return;
        }

        if (Interlocked.CompareExchange(ref this._isInputClosed, 1, 0) == 0)
        {
            TryClose(this._process.StandardInput, sendStop: true);
        }

        try
        {
            this._process.WaitForExit(5000);
        }
        catch
        {
            // ignored, the guardian is a best-effort helper
        }

        this._process.Dispose();
    }

    private static void TryClose(StreamWriter input, bool sendStop)
    {
        try
        {
            if (sendStop)
            {
                input.WriteLine(StopCommand);
                input.Flush();
            }

            input.Close();
        }
        catch
        {
            // The guardian already exited; there is nothing left to stop
        }
    }
}
