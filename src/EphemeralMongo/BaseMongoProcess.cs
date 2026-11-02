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

    private UnixProcessGuardian? Guardian { get; set; }

    // Called right after the process starts, before waiting for it to become ready, so that a host killed during
    // startup does not leave the process behind either. Windows relies on the job object instead.
    protected void OnMongodProcessStarted()
    {
        if (this.Guardian != null || !UnixProcessGuardian.IsSupported)
        {
            return;
        }

        var dataDirectory = this.Options.ResolvedDataDirectory;
        var commandLineMarker = string.IsNullOrEmpty(dataDirectory) ? "mongod" : dataDirectory!;
        var directoryToDelete = this.Options.IsDataDirectoryOwnedByRunner ? dataDirectory : null;

        this.Guardian = UnixProcessGuardian.TryStart(this.Process.Id, directoryToDelete, commandLineMarker, this.Options.StandardErrorLogger);
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
            // Stop the guardian only once the process is gone, so it keeps protecting it until the very end
            this.Guardian?.Dispose();

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