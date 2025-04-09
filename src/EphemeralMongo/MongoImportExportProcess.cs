namespace EphemeralMongo;

internal sealed class MongoImportExportProcess : BaseMongoProcess
{
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
        this.Process.Start();

        this.Process.BeginOutputReadLine();
        this.Process.BeginErrorReadLine();

        // Wait for the end of import or export
#if NET8_0_OR_GREATER
        await this.Process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
#else
        this.Process.WaitForExit();
        return Task.CompletedTask;
#endif
    }
}