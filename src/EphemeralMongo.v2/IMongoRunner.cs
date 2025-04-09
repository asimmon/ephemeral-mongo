namespace EphemeralMongo;

public interface IMongoRunner : IDisposable
{
    string ConnectionString { get; }

    Task ImportAsync(string database, string collection, string inputFilePath, string[]? additionalArguments = null, bool drop = false, CancellationToken cancellationToken = default);

    Task ExportAsync(string database, string collection, string outputFilePath, string[]? additionalArguments = null, CancellationToken cancellationToken = default);
}