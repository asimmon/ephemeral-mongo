using System.Diagnostics;

namespace EphemeralMongo;

internal abstract class BaseMongoProcess : IMongoProcess
{
    protected BaseMongoProcess(MongoRunnerOptions options, string executablePath, string arguments)
    {
        this.Options = options;

        NativeMethods.EnsureMongoProcessesAreKilledWhenCurrentProcessIsKilled();

        var processStartInfo = new ProcessStartInfo
        {
            FileName = executablePath,
            Arguments = arguments,
            CreateNoWindow = true,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };

        this.Process = new Process
        {
            StartInfo = processStartInfo,
        };

        this.Process.OutputDataReceived += this.OnOutputDataReceivedForLogging;
        this.Process.ErrorDataReceived += this.OnErrorDataReceivedForLogging;
    }

    protected MongoRunnerOptions Options { get; }

    protected Process Process { get; }

    private void OnOutputDataReceivedForLogging(object sender, DataReceivedEventArgs args)
    {
        if (this.Options.StandardOuputLogger != null && args.Data != null)
        {
            this.Options.StandardOuputLogger(args.Data);
        }
    }

    private void OnErrorDataReceivedForLogging(object sender, DataReceivedEventArgs args)
    {
        if (this.Options.StandardErrorLogger != null && args.Data != null)
        {
            this.Options.StandardErrorLogger(args.Data);
        }
    }

    public abstract void Start();

    public void Dispose()
    {
        this.Process.OutputDataReceived -= this.OnOutputDataReceivedForLogging;
        this.Process.ErrorDataReceived -= this.OnErrorDataReceivedForLogging;

        this.Process.CancelOutputRead();
        this.Process.CancelErrorRead();

        if (!this.Process.HasExited)
        {
            try
            {
                this.Process.Kill();
                this.Process.WaitForExit();
            }
            catch
            {
                // ignored, we did our best to stop the process
            }
        }

        this.Process.Dispose();
    }
}