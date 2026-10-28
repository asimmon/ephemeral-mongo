using System.Diagnostics;
using System.Text;

namespace EphemeralMongo;

internal sealed class MongoImportExportProcess : BaseMongoProcess
{
    private readonly StringBuilder _output = new StringBuilder();

    public MongoImportExportProcess(MongoRunnerOptions options, string executablePath, string[] arguments)
        : base(options, executablePath, arguments)
    {
    }

#if NET8_0_OR_GREATER
    public override async Task StartAsync(CancellationToken cancellationToken)
#else
    public override Task StartAsync(CancellationToken cancellationToken)
#endif
    {
        this.Process.OutputDataReceived += this.OnDataReceivedCaptureOutput;
        this.Process.ErrorDataReceived += this.OnDataReceivedCaptureOutput;

        try
        {
            this.Process.Start();

            this.Process.BeginOutputReadLine();
            this.Process.BeginErrorReadLine();

            // Wait for the end of import or export
#if NET8_0_OR_GREATER
            await this.Process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
#else
            this.Process.WaitForExit();
#endif

            // mongoimport and mongoexport report failures (missing input file, malformed JSON, invalid arguments, unwritable output path, etc.)
            // only through a non-zero exit code and messages written to standard error
            if (this.Process.ExitCode != 0)
            {
                throw this.CreateExceptionFromFailedProcessExit();
            }
        }
        finally
        {
            this.Process.OutputDataReceived -= this.OnDataReceivedCaptureOutput;
            this.Process.ErrorDataReceived -= this.OnDataReceivedCaptureOutput;
        }

#if !NET8_0_OR_GREATER
        return Task.CompletedTask;
#endif
    }

    private EphemeralMongoException CreateExceptionFromFailedProcessExit()
    {
        var exitCode = this.Process.ExitCode;
        var processDescription = $"{this.Process.StartInfo.FileName} {this.Process.StartInfo.Arguments}";

        string output;
        lock (this._output)
        {
            output = this._output.ToString();
        }

        var outputMessage = string.IsNullOrWhiteSpace(output) ? string.Empty : $" Output: {output}";

        return new EphemeralMongoException($"The MongoDB process '{processDescription}' failed with exit code {exitCode}.{outputMessage}");
    }

    private void OnDataReceivedCaptureOutput(object sender, DataReceivedEventArgs args)
    {
        if (args.Data != null)
        {
            // Standard output and error events are raised on different threads
            lock (this._output)
            {
                this._output.AppendLine(args.Data);
            }
        }
    }
}
