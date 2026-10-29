using System.Diagnostics;
using System.Text;

namespace EphemeralMongo;

internal abstract class BaseMongoProcess : IMongoProcess
{
    private readonly string _executablePath;
    private readonly KillOnParentExitMode _killOnParentExitMode;

    protected BaseMongoProcess(MongoRunnerOptions options, string executablePath, string[] arguments)
    {
        this._executablePath = executablePath;
        this.Options = options;

        NativeMethods.EnsureMongoProcessesAreKilledWhenCurrentProcessIsKilled();

        var joinedArguments = string.Join(" ", arguments);

        // The process start info might be modified to kill the process when the current process exits, so we keep the original command for error messages
        this.ProcessDescription = $"{executablePath} {joinedArguments}";

        var processStartInfo = new ProcessStartInfo
        {
            FileName = executablePath,
            Arguments = joinedArguments,
            CreateNoWindow = true,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };

        this._killOnParentExitMode = KillOnParentExit.Configure(processStartInfo, options.KillMongoProcessesWhenCurrentProcessExits);

        this.Process = new Process
        {
            StartInfo = processStartInfo,
            EnableRaisingEvents = true,
        };

        this.Process.OutputDataReceived += this.OnOutputDataReceivedForLogging;
        this.Process.ErrorDataReceived += this.OnErrorDataReceivedForLogging;
    }

    protected MongoRunnerOptions Options { get; }

    protected Process Process { get; }

    protected string ProcessDescription { get; }

    protected void StartProcess()
    {
        KillOnParentExit.Start(this.Process, this._executablePath, this._killOnParentExitMode);
    }

    // Tells users how to disable killing MongoDB processes when the current process exits, if it might have caused this process to fail
    protected string GetKillOnParentExitTroubleshootingMessage(string output)
    {
        return KillOnParentExit.GetTroubleshootingMessage(this._killOnParentExitMode, this.Process.ExitCode, output);
    }

    private void OnOutputDataReceivedForLogging(object sender, DataReceivedEventArgs args)
    {
        if (this.Options.StandardOutputLogger != null && args.Data != null)
        {
            this.Options.StandardOutputLogger(args.Data);
        }
    }

    private void OnErrorDataReceivedForLogging(object sender, DataReceivedEventArgs args)
    {
        if (this.Options.StandardErrorLogger != null && args.Data != null)
        {
            this.Options.StandardErrorLogger(args.Data);
        }
    }

    public abstract Task StartAsync(CancellationToken cancellationToken);

    public void Dispose()
    {
        this.Process.OutputDataReceived -= this.OnOutputDataReceivedForLogging;
        this.Process.ErrorDataReceived -= this.OnErrorDataReceivedForLogging;

        // Dispose can run while an exception thrown by StartAsync is unwinding (e.g. Process.Start failed because the executable is missing),
        // so none of the cleanup steps below may throw, otherwise the original exception would be replaced and Process.Dispose skipped
        try
        {
            TryCancelAsyncRead(this.Process.CancelOutputRead);
            TryCancelAsyncRead(this.Process.CancelErrorRead);

            if (!this.Process.HasExited)
            {
                this.Process.Kill();
                this.Process.WaitForExit();
            }
        }
        catch
        {
            // Ignored, we did our best to stop the process (HasExited throws if the process was never started)
        }
        finally
        {
            this.Process.Dispose();
        }
    }

    private static void TryCancelAsyncRead(Action cancelAsyncRead)
    {
        try
        {
            cancelAsyncRead();
        }
        catch (InvalidOperationException)
        {
            // The process was never started or the asynchronous read was never started
        }
    }
}