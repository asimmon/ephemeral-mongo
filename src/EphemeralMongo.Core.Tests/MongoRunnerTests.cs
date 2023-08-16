using System.Text.Json;
using System.Text.Json.Serialization;
using GSoft.Extensions.Xunit;
using Microsoft.Extensions.Logging;
using MongoDB.Driver;
using Xunit;
using Xunit.Abstractions;

namespace EphemeralMongo.Core.Tests;

public class MongoRunnerTests : BaseIntegrationTest
{
    public MongoRunnerTests(EmptyIntegrationFixture fixture, ITestOutputHelper testOutputHelper)
        : base(fixture, testOutputHelper)
    {
    }

    [Fact]
    public void Run_Fails_When_BinaryDirectory_Does_Not_Exist()
    {
        var options = new MongoRunnerOptions
        {
            StandardOuputLogger = this.MongoMessageLogger,
            StandardErrorLogger = this.MongoMessageLogger,
            BinaryDirectory = Guid.NewGuid().ToString(),
            AdditionalArguments = "--quiet",
            KillMongoProcessesWhenCurrentProcessExits = true,
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
            StandardOuputLogger = this.MongoMessageLogger,
            StandardErrorLogger = this.MongoMessageLogger,
            AdditionalArguments = "--quiet",
            KillMongoProcessesWhenCurrentProcessExits = true,
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

    private void MongoMessageLogger(string message)
    {
        try
        {
            var trace = JsonSerializer.Deserialize<MongoTrace>(message);

            if (trace != null && !string.IsNullOrEmpty(trace.Message))
            {
                // https://www.mongodb.com/docs/manual/reference/log-messages/#std-label-log-severity-levels
                var logLevel = trace.Severity switch
                {
                    "F" => LogLevel.Critical,
                    "E" => LogLevel.Error,
                    "W" => LogLevel.Warning,
                    _ => LogLevel.Information,
                };

                const int longestComponentNameLength = 8;
                this.Logger.Log(logLevel, "{Component} {Message}", trace.Component.PadRight(longestComponentNameLength), trace.Message);
                return;
            }
        }
        catch (JsonException)
        {
        }

        this.Logger.LogInformation("{Message}", message);
    }

    private sealed class MongoTrace
    {
        [JsonPropertyName("s")]
        public string Severity { get; set; } = string.Empty;

        [JsonPropertyName("c")]
        public string Component { get; set; } = string.Empty;

        [JsonPropertyName("msg")]
        public string Message { get; set; } = string.Empty;
    }

    private sealed class Person
    {
        public Person()
        {
        }

        public Person(string id, string name)
        {
            this.Id = id;
            this.Name = name;
        }

        public string Id { get; set; } = string.Empty;

        public string Name { get; set; } = string.Empty;

        private bool Equals(Person other)
        {
            return this.Id == other.Id && this.Name == other.Name;
        }

        public override bool Equals(object? obj)
        {
            return ReferenceEquals(this, obj) || (obj is Person other && this.Equals(other));
        }
    }
}