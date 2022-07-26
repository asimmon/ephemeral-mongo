namespace Askaiser.EphemeralMongo.Core;

internal sealed class MongoProcessFactory : IMongoProcessFactory
{
    public IMongoProcess CreateMongoProcess(MongoRunnerOptions options)
    {
        return new MongoProcess(options);
    }

    public IMongoProcess CreateMongoImportExportProcess(MongoRunnerOptions options, string executablePath, string arguments)
    {
        return new MongoImportExportProcess(options, executablePath, arguments);
    }
}