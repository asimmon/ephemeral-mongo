namespace EphemeralMongo.Core;

internal sealed class MongoProcessFactory : IMongoProcessFactory
{
    public IMongoProcess CreateMongoProcess(MongoRunnerOptions options, MongoProcessKind processKind, string executablePath, string arguments)
    {
        return processKind == MongoProcessKind.Mongod
            ? new MongodProcess(options, executablePath, arguments)
            : new MongoImportExportProcess(options, executablePath, arguments);
    }
}