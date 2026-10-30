using System.Diagnostics;
using System.Text;
using EphemeralMongo.KillOnParentExit;

namespace EphemeralMongo;

internal abstract class BaseMongoProcess : IMongoProcess
{
    private readonly string _executablePath;
    private readonly IKillOnParentExitStrategy _killOnParentExit;

    protected BaseMongoProcess(MongoRunnerOptions options, string executablePath, string[] arguments)
    {
        this._executablePath = executablePath;
        this.Options = options;

        var joinedArguments = string.Join(" ", arguments);

        // Not taken from the start info, which can be changed to start the process through setpriv
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

        this._killOnParentExit = KillOnParentExitStrategies.Configure(processStartInfo, options.KillMongoProcessesWhenCurrentProcessExits);

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
        this._killOnParentExit.Start(this.Process, this._executablePath);
    }

    protected string GetKillOnParentExitTroubleshootingMessage(string output)
    {
        return this._killOnParentExit.GetTroubleshootingMessage(this.Process.ExitCode, output);
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