namespace EphemeralMongo;

internal interface IMongoExecutableLocator
{
    Task<string> FindMongoExecutablePathAsync(MongoRunnerOptions options, MongoProcessKind processKind, CancellationToken cancellationToken);
}