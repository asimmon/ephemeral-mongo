namespace EphemeralMongo;

internal interface IMongoProcess : IDisposable
{
    void Start();
}