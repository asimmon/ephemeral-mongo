using System.Diagnostics;

namespace Askaiser.EphemeralMongo.Core;

internal sealed class MongoImportExportProcess : IMongoProcess
{
    private readonly MongoRunnerOptions _options;
    private readonly Process _process;

    public MongoImportExportProcess(MongoRunnerOptions options, string executablePath, string arguments)
    {
        this._options = options;

        var processStartInfo = new ProcessStartInfo
        {
            FileName = executablePath,
            Arguments = arguments,
            CreateNoWindow = true,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };

        this._process = new Process
        {
            StartInfo = processStartInfo,
        };

        this._process.OutputDataReceived += this.OnOutputDataReceivedForLogging;
        this._process.ErrorDataReceived += this.OnErrorDataReceivedForLogging;
    }

    private void OnOutputDataReceivedForLogging(object sender, DataReceivedEventArgs args)
    {
        if (this._options.StandardOuputLogger != null && args.Data != null)
        {
            this._options.StandardOuputLogger(args.Data);
        }
    }

    private void OnErrorDataReceivedForLogging(object sender, DataReceivedEventArgs args)
    {
        if (this._options.StandardErrorLogger != null && args.Data != null)
        {
            this._options.StandardErrorLogger(args.Data);
        }
    }

    public void Start()
    {
        this._process.Start();

        this._process.BeginOutputReadLine();
        this._process.BeginErrorReadLine();
    }

    public void Dispose()
    {
        this._process.OutputDataReceived -= this.OnOutputDataReceivedForLogging;
        this._process.ErrorDataReceived -= this.OnErrorDataReceivedForLogging;

        this._process.CancelOutputRead();
        this._process.CancelErrorRead();

        if (!this._process.HasExited)
        {
            try
            {
                this._process.Kill();
                this._process.WaitForExit();
            }
            catch
            {
                // ignored, we did our best to stop the process
            }
        }

        this._process.Dispose();
    }
}