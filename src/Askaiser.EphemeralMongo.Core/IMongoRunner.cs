namespace Askaiser.EphemeralMongo.Core;

public interface IMongoRunner : IDisposable
{
    public string ConnectionString { get; }
}