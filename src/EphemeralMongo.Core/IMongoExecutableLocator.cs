namespace EphemeralMongo.Core;

internal interface IMongoExecutableLocator
{
    string FindMongoExecutablePath(MongoRunnerOptions options, MongoProcessKind processKind);
}