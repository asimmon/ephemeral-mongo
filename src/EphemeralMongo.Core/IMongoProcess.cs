namespace EphemeralMongo.Core;

internal interface IMongoProcess : IDisposable
{
    void Start();
}