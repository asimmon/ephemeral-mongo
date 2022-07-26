using Microsoft.Extensions.Logging;
using MongoDB.Driver;
using ShareGate.Extensions.Xunit;
using Xunit;
using Xunit.Abstractions;

namespace Askaiser.EphemeralMongo.Core.Tests;

public class MongoRunnerTests : BaseIntegrationTest
{
    public MongoRunnerTests(ITestOutputHelper testOutputHelper)
        : base(testOutputHelper)
    {
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Everything_Works(bool useSingleNodeReplicaSet)
    {
        const string databaseName = "default";
        const string collectionName = "people";

        var options = new MongoRunnerOptions
        {
            UseSingleNodeReplicaSet = useSingleNodeReplicaSet,
            StandardOuputLogger = x => this.Logger.LogInformation("{X}", x),
            StandardErrorLogger = x => this.Logger.LogInformation("{X}", x),
        };

        var originalPerson = new Person("john", "John Doe");
        var exportedFilePath = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());

        try
        {
            using (var runner = MongoRunner.Run(options))
            {
                var database = new MongoClient(runner.ConnectionString).GetDatabase(databaseName);

                // Verify that the collection is empty
                var personBeforeImport = database.GetCollection<Person>(collectionName).Find(FilterDefinition<Person>.Empty).FirstOrDefault();
                Assert.Null(personBeforeImport);

                // Add a document
                database.GetCollection<Person>(collectionName).InsertOne(new Person(originalPerson.Id, originalPerson.Name));
                runner.Export(databaseName, collectionName, exportedFilePath);

                // Verify that the document was inserted successfully
                var personAfterImport = database.GetCollection<Person>(collectionName).Find(FilterDefinition<Person>.Empty).FirstOrDefault();
                Assert.Equal(originalPerson, personAfterImport);
            }

            using (var runner = MongoRunner.Run(options))
            {
                var database = new MongoClient(runner.ConnectionString).GetDatabase(databaseName);

                // Verify that the collection is empty
                var personBeforeImport = database.GetCollection<Person>(collectionName).Find(FilterDefinition<Person>.Empty).FirstOrDefault();
                Assert.Null(personBeforeImport);

                // Import the exported collection
                runner.Import(databaseName, collectionName, exportedFilePath);

                // Verify that the document was imported successfully
                var personAfterImport = database.GetCollection<Person>(collectionName).Find(FilterDefinition<Person>.Empty).FirstOrDefault();
                Assert.Equal(originalPerson, personAfterImport);
            }
        }
        finally
        {
            File.Delete(exportedFilePath);
        }
    }

    private sealed record Person(string Id, string Name);
}