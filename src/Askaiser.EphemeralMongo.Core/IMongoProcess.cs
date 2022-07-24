namespace Askaiser.EphemeralMongo.Core;

internal interface IMongoProcess : IDisposable
{
    void Start();
}