using Microsoft.Extensions.Logging;
using MongoDB.Driver;
using ShareGate.Extensions.Xunit;
using Xunit;
using Xunit.Abstractions;

namespace EphemeralMongo.Core.Tests;

public class MongoRunnerTests : BaseIntegrationTest
{
    public MongoRunnerTests(ITestOutputHelper testOutputHelper)
        : base(testOutputHelper)
    {
    }

    [Fact]
    public void Run_Fails_When_BinaryDirectory_Does_Not_Exist()
    {
        var options = new MongoRunnerOptions
        {
            StandardOuputLogger = x => this.Logger.LogInformation("{X}", x),
            StandardErrorLogger = x => this.Logger.LogInformation("{X}", x),
            BinaryDirectory = Guid.NewGuid().ToString(),
        };

        IMongoRunner? runner = null;

        try
        {
            var ex = Assert.Throws<FileNotFoundException>(() => runner = MongoRunner.Run(options));
            Assert.Contains(options.BinaryDirectory, ex.ToString());
            Assert.DoesNotContain("runtimes", ex.ToString());
        }
        finally
        {
            runner?.Dispose();
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Import_Export_Works(bool useSingleNodeReplicaSet)
    {
        const string databaseName = "default";
        const string collectionName = "people";

        var options = new MongoRunnerOptions
        {
            UseSingleNodeReplicaSet = useSingleNodeReplicaSet,
            StandardOuputLogger = x => this.Logger.LogInformation("{X}", x),
            StandardErrorLogger = x => this.Logger.LogInformation("{X}", x),
        };

        using (var runner = MongoRunner.Run(options))
        {
            if (useSingleNodeReplicaSet)
            {
                Assert.Contains("replicaSet", runner.ConnectionString);
            }
            else
            {
                Assert.DoesNotContain("replicaSet", runner.ConnectionString);
            }
        }

        var originalPerson = new Person("john", "John Doe");
        var exportedFilePath = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());

        try
        {
            using (var runner1 = MongoRunner.Run(options))
            {
                var database = new MongoClient(runner1.ConnectionString).GetDatabase(databaseName);

                // Verify that the collection is empty
                var personBeforeImport = database.GetCollection<Person>(collectionName).Find(FilterDefinition<Person>.Empty).FirstOrDefault();
                Assert.Null(personBeforeImport);

                // Add a document
                database.GetCollection<Person>(collectionName).InsertOne(new Person(originalPerson.Id, originalPerson.Name));
                runner1.Export(databaseName, collectionName, exportedFilePath);

                // Verify that the document was inserted successfully
                var personAfterImport = database.GetCollection<Person>(collectionName).Find(FilterDefinition<Person>.Empty).FirstOrDefault();
                Assert.Equal(originalPerson, personAfterImport);
            }

            IMongoRunner runner2;
            using (runner2 = MongoRunner.Run(options))
            {
                var database = new MongoClient(runner2.ConnectionString).GetDatabase(databaseName);

                // Verify that the collection is empty
                var personBeforeImport = database.GetCollection<Person>(collectionName).Find(FilterDefinition<Person>.Empty).FirstOrDefault();
                Assert.Null(personBeforeImport);

                // Import the exported collection
                runner2.Import(databaseName, collectionName, exportedFilePath);

                // Verify that the document was imported successfully
                var personAfterImport = database.GetCollection<Person>(collectionName).Find(FilterDefinition<Person>.Empty).FirstOrDefault();
                Assert.Equal(originalPerson, personAfterImport);
            }

            // Disposing twice does nothing
            runner2.Dispose();

            // Can't use import or export if already disposed
            Assert.Throws<ObjectDisposedException>(() => runner2.Export("whatever", "whatever", "whatever.json"));
            Assert.Throws<ObjectDisposedException>(() => runner2.Import("whatever", "whatever", "whatever.json"));
        }
        finally
        {
            File.Delete(exportedFilePath);
        }
    }

    private sealed record Person(string Id, string Name);
}