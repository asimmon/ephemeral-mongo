namespace Askaiser.EphemeralMongo.Core;

internal sealed class MongoProcessFactory : IMongoProcessFactory
{
    public IMongoProcess Create(MongoRunnerOptions options)
    {
        return new MongoProcess(options);
    }
}