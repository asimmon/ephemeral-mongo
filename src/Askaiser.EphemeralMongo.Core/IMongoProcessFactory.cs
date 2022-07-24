namespace Askaiser.EphemeralMongo.Core;

internal interface IMongoProcessFactory
{
    IMongoProcess Create(MongoRunnerOptions options);
}