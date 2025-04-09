using System.Diagnostics.CodeAnalysis;
using System.Text.Json;
using System.Text.Json.Serialization;
using MongoDB.Driver;

namespace EphemeralMongo.Tests;

public class MongoRunnerTests(ITestOutputHelper testOutputHelper, ITestContextAccessor testContextAccessor)
{
    [Fact]
    public async Task Run_Fails_When_BinaryDirectory_Does_Not_Exist()
    {
        var options = new MongoRunnerOptions
        {
            StandardOutputLogger = this.MongoMessageLogger,
            StandardErrorLogger = this.MongoMessageLogger,
            BinaryDirectory = Guid.NewGuid().ToString(),
            AdditionalArguments = ["--quiet"],
        };

        IMongoRunner? runner = null;

        try
        {
            var ex = await Assert.ThrowsAsync<FileNotFoundException>(async () =>
            {
                runner = await MongoRunner.RunAsync(options, testContextAccessor.Current.CancellationToken);
            });

            Assert.Contains(options.BinaryDirectory, ex.ToString());
            Assert.DoesNotContain("runtimes", ex.ToString());
        }
        finally
        {
            runner?.Dispose();
        }
    }

    [Fact]
    public async Task Run_Cleans_Up_Temporary_Data_Directory()
    {
        // TODO this conflicts in multitarget builds
        var rootDataDirectoryPath = Path.Combine(Path.GetTempPath(), "ephemeral-mongo-data-cleanup-tests");

        try
        {
            // Start with a clean slate
            Directory.Delete(rootDataDirectoryPath, recursive: true);
        }
        catch (DirectoryNotFoundException)
        {
        }

        var options = new MongoRunnerOptions
        {
            StandardOutputLogger = this.MongoMessageLogger,
            StandardErrorLogger = this.MongoMessageLogger,
            RootDataDirectoryPath = rootDataDirectoryPath,
            AdditionalArguments = ["--quiet"],
        };

        testOutputHelper.WriteLine("Root data directory path: {0}", options.RootDataDirectoryPath);
        Assert.False(Directory.Exists(options.RootDataDirectoryPath), "The root data directory should not exist yet.");

        // Creating a first data directory
        using (await MongoRunner.RunAsync(options, testContextAccessor.Current.CancellationToken))
        {
        }

        // Creating another data directory
        using (await MongoRunner.RunAsync(options, testContextAccessor.Current.CancellationToken))
        {
        }

        // Assert there's now two data directories
        var dataDirectories = new HashSet<string>(Directory.EnumerateDirectories(options.RootDataDirectoryPath), StringComparer.Ordinal);
        testOutputHelper.WriteLine("Data directories: {0}", string.Join(", ", dataDirectories));
        Assert.Equal(2, dataDirectories.Count);

        // Shorten the lifetime of the data directories and wait for a longer time
        options.DataDirectoryLifetime = TimeSpan.FromSeconds(1);
        await Task.Delay(TimeSpan.FromSeconds(2), testContextAccessor.Current.CancellationToken);

        // This should delete the old data directories and create a new one
        using (await MongoRunner.RunAsync(options, testContextAccessor.Current.CancellationToken))
        {
        }

        var dataDirectoriesAfterCleanup = new HashSet<string>(Directory.EnumerateDirectories(options.RootDataDirectoryPath), StringComparer.Ordinal);
        testOutputHelper.WriteLine("Data directories after cleanup: {0}", string.Join(", ", dataDirectoriesAfterCleanup));

        var thirdDataDirectory = Assert.Single(dataDirectoriesAfterCleanup);
        Assert.DoesNotContain(thirdDataDirectory, dataDirectories);
    }

    [Theory]
    [InlineData(false, MongoVersion.V6, MongoEdition.Community)]
    [InlineData(false, MongoVersion.V7, MongoEdition.Community)]
    [InlineData(false, MongoVersion.V8, MongoEdition.Community)]
    [InlineData(false, MongoVersion.V6, MongoEdition.Enterprise)]
    [InlineData(false, MongoVersion.V7, MongoEdition.Enterprise)]
    [InlineData(false, MongoVersion.V8, MongoEdition.Enterprise)]
    [InlineData(true, MongoVersion.V6, MongoEdition.Community)]
    [InlineData(true, MongoVersion.V7, MongoEdition.Community)]
    [InlineData(true, MongoVersion.V8, MongoEdition.Community)]
    [InlineData(true, MongoVersion.V6, MongoEdition.Enterprise)]
    [InlineData(true, MongoVersion.V7, MongoEdition.Enterprise)]
    [InlineData(true, MongoVersion.V8, MongoEdition.Enterprise)]
    public async Task Import_Export_Works(bool replset, MongoVersion version, MongoEdition edition)
    {
        const string databaseName = "default";
        const string collectionName = "people";

        var options = new MongoRunnerOptions
        {
            Version = version,
            Edition = edition,
            UseSingleNodeReplicaSet = replset,
            StandardOutputLogger = this.MongoMessageLogger,
            StandardErrorLogger = this.MongoMessageLogger,
            AdditionalArguments = edition == MongoEdition.Enterprise
                ? ["--quiet", "--storageEngine", "inMemory"]
                : ["--quiet"],
        };

        using (var runner = await MongoRunner.RunAsync(options, testContextAccessor.Current.CancellationToken))
        {
            if (replset)
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
            using (var runner1 = await MongoRunner.RunAsync(options, testContextAccessor.Current.CancellationToken))
            {
                var database = new MongoClient(runner1.ConnectionString).GetDatabase(databaseName);

                // Verify that the collection is empty
                var personBeforeImport = database.GetCollection<Person>(collectionName).Find(FilterDefinition<Person>.Empty).FirstOrDefault(testContextAccessor.Current.CancellationToken);
                Assert.Null(personBeforeImport);

                // Add a document
                await database.GetCollection<Person>(collectionName).InsertOneAsync(new Person(originalPerson.Id, originalPerson.Name), options: null, cancellationToken: testContextAccessor.Current.CancellationToken);
                await runner1.ExportAsync(databaseName, collectionName, exportedFilePath, ["--jsonArray"], testContextAccessor.Current.CancellationToken);

                // Verify that the document was inserted successfully
                var personAfterImport = database.GetCollection<Person>(collectionName).Find(FilterDefinition<Person>.Empty).FirstOrDefault(testContextAccessor.Current.CancellationToken);
                Assert.Equal(originalPerson, personAfterImport);
            }

            IMongoRunner runner2;
            using (runner2 = await MongoRunner.RunAsync(options, testContextAccessor.Current.CancellationToken))
            {
                var database = new MongoClient(runner2.ConnectionString).GetDatabase(databaseName);

                // Verify that the collection is empty
                var personBeforeImport = database.GetCollection<Person>(collectionName).Find(FilterDefinition<Person>.Empty).FirstOrDefault(testContextAccessor.Current.CancellationToken);
                Assert.Null(personBeforeImport);

                // Import the exported collection
                await runner2.ImportAsync(databaseName, collectionName, exportedFilePath, ["--jsonArray"], cancellationToken: testContextAccessor.Current.CancellationToken);

                // Verify that the document was imported successfully
                var personAfterImport = database.GetCollection<Person>(collectionName).Find(FilterDefinition<Person>.Empty).FirstOrDefault(testContextAccessor.Current.CancellationToken);
                Assert.Equal(originalPerson, personAfterImport);
            }

            // Disposing twice does nothing
            runner2.Dispose();

            // Can't use import or export if already disposed
            await Assert.ThrowsAsync<ObjectDisposedException>(() => runner2.ExportAsync("whatever", "whatever", "whatever.json", cancellationToken: testContextAccessor.Current.CancellationToken));
            await Assert.ThrowsAsync<ObjectDisposedException>(() => runner2.ImportAsync("whatever", "whatever", "whatever.json", cancellationToken: testContextAccessor.Current.CancellationToken));
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
                    "F" => "CTR",
                    "E" => "ERR",
                    "W" => "WRN",
                    _ => "INF",
                };

                const int longestComponentNameLength = 8;
                testOutputHelper.WriteLine("{0} {1} {2}", logLevel, trace.Component.PadRight(longestComponentNameLength), trace.Message);
                return;
            }
        }
        catch (JsonException)
        {
        }

        testOutputHelper.WriteLine(message);
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

    private sealed record Person(string Id, string Name)
    {
        [SuppressMessage("ReSharper", "UnusedMember.Local", Justification = "Used by MongoDB deserialization")]
        public Person()
            : this(string.Empty, string.Empty)
        {
        }
    }
}