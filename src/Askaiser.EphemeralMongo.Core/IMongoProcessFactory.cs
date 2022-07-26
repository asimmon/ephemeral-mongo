namespace Askaiser.EphemeralMongo.Core;

internal interface IMongoProcessFactory
{
    IMongoProcess CreateMongoProcess(MongoRunnerOptions options);

    IMongoProcess CreateMongoImportExportProcess(MongoRunnerOptions options, string executablePath, string arguments);
}