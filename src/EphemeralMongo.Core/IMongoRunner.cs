namespace EphemeralMongo.Core;

public interface IMongoRunner : IDisposable
{
    string ConnectionString { get; }

    void Import(string database, string collection, string inputFilePath, string? additionalArguments = null, bool drop = false);

    void Export(string database, string collection, string outputFilePath, string? additionalArguments = null);
}