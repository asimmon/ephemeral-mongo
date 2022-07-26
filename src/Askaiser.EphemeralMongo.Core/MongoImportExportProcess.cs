namespace Askaiser.EphemeralMongo.Core;

internal sealed class MongoImportExportProcess : BaseMongoProcess
{
    public MongoImportExportProcess(MongoRunnerOptions options, string executablePath, string arguments)
        : base(options, executablePath, arguments)
    {
    }

    public override void Start()
    {
        this.Process.Start();

        this.Process.BeginOutputReadLine();
        this.Process.BeginErrorReadLine();

        // Wait for the end of import or export
        this.Process.WaitForExit();
    }
}