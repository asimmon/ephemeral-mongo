namespace EphemeralMongo;

internal interface IMongoProcess : IDisposable
{
    Task StartAsync(CancellationToken cancellationToken);
}