using System.Diagnostics.CodeAnalysis;
using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Text.Json.Serialization;
using EphemeralMongo.Download;
using MongoDB.Driver;

namespace EphemeralMongo.Tests;

public class MongoRunnerTests(ITestOutputHelper testOutputHelper, ITestContextAccessor testContextAccessor)
{
    [Fact]
    public async Task StartMongo_WithNonExistentBinaryDirectory_ThrowsFileNotFoundException()
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
    public async Task StartMongo_WithTemporaryDataDirectory_CleansUpOldDirectories()
    {
        var rootDataDirectoryPath = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());

        try
        {
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
        finally
        {
            try
            {
                Directory.Delete(rootDataDirectoryPath, recursive: true);
            }
            catch (DirectoryNotFoundException)
            {
            }
        }
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
    public async Task MongoOperations_ImportAndExport_SucceedsAcrossInstances(bool replset, MongoVersion version, MongoEdition edition)
    {
        if (version is MongoVersion.V6 or MongoVersion.V7 && edition == MongoEdition.Enterprise && RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
        {
            var target = DownloadTargetHelper.Instance.GetOriginalLinuxTarget();
            if (target == "ubuntu2404")
            {
                Assert.Skip("Enterprise MongoDB 6 and 7 are not supported on Ubuntu 24.04");
            }
        }

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

#pragma warning disable CA1849 // We want to test synchronous methods
            IMongoRunner runner2;
            using (runner2 = MongoRunner.Run(options, testContextAccessor.Current.CancellationToken))
            {
                var database = new MongoClient(runner2.ConnectionString).GetDatabase(databaseName);

                // Verify that the collection is empty
                var personBeforeImport = database.GetCollection<Person>(collectionName).Find(FilterDefinition<Person>.Empty).FirstOrDefault(testContextAccessor.Current.CancellationToken);
                Assert.Null(personBeforeImport);

                // Import the exported collection
                runner2.Import(databaseName, collectionName, exportedFilePath, ["--jsonArray"], drop: false, testContextAccessor.Current.CancellationToken);

                // Verify that the document was imported successfully
                var personAfterImport = database.GetCollection<Person>(collectionName).Find(FilterDefinition<Person>.Empty).FirstOrDefault(testContextAccessor.Current.CancellationToken);
                Assert.Equal(originalPerson, personAfterImport);
            }
#pragma warning restore CA1849

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

    [Fact]
    public async Task StartMongo_WithInvalidArgument_ThrowsExceptionWithDetails()
    {
        var options = new MongoRunnerOptions
        {
            Version = MongoVersion.V8,
            StandardOutputLogger = this.MongoMessageLogger,
            StandardErrorLogger = this.MongoMessageLogger,
            AdditionalArguments = ["--invalid-argument"],
        };

        var ex = await Assert.ThrowsAsync<EphemeralMongoException>(async () =>
        {
            using var _ = await MongoRunner.RunAsync(options, testContextAccessor.Current.CancellationToken);
        });

        testOutputHelper.WriteLine(ex.Message);
        Assert.Contains("unrecognised option '--invalid-argument'", ex.Message);
    }

    [Fact]
    public async Task StartMongo_WithPortInUse_ThrowsExceptionWithDetails()
    {
        var listener = new TcpListener(IPAddress.Loopback, port: 0);

        try
        {
            listener.Start();
            var port = ((IPEndPoint)listener.LocalEndpoint).Port;

            var options = new MongoRunnerOptions
            {
                Version = MongoVersion.V8,
                StandardOutputLogger = testOutputHelper.WriteLine,
                StandardErrorLogger = testOutputHelper.WriteLine,
                AdditionalArguments = ["--quiet"],
                MongoPort = port,
            };

            var ex = await Assert.ThrowsAsync<EphemeralMongoException>(async () =>
            {
                using var _ = await MongoRunner.RunAsync(options, testContextAccessor.Current.CancellationToken);
            });

            testOutputHelper.WriteLine(ex.Message);

            // Full message looks like this:
            // The MongoDB process '<omitted>' exited unexpectedly with code 48. Output:{"t":{"$date":"2025-04-18T22:44:09.346-04:00"},"s":"E",  "c":"CONTROL",  "id":20568,   "ctx":"initandlisten","msg":"Error setting up listener","attr":{"error":{"code":9001,"codeName":"SocketException","errmsg":"127.0.0.1:60912 :: caused by :: setup bind :: caused by :: An attempt was made to access a socket in a way forbidden by its access permissions."}}}
            Assert.Contains("SocketException", ex.Message);
        }
        finally
        {
            listener.Stop();
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