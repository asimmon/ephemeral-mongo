using System.Diagnostics;
using System.Text;

namespace EphemeralMongo;

internal abstract class BaseMongoProcess : IMongoProcess
{
    protected BaseMongoProcess(MongoRunnerOptions options, string executablePath, string[] arguments)
    {
        this.Options = options;

        NativeMethods.EnsureMongoProcessesAreKilledWhenCurrentProcessIsKilled();

        var processStartInfo = new ProcessStartInfo
        {
            FileName = executablePath,
            Arguments = string.Join(" ", arguments),
            CreateNoWindow = true,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };

        KillOnParentExit.EnableIfSupported(processStartInfo);

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