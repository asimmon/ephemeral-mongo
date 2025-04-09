using System.Runtime.InteropServices;
using EphemeralMongo.Download;
using MongoDB.Driver;

namespace EphemeralMongo.Tests;

public abstract class MongoRunnerImportExportTests(
    ITestOutputHelper testOutputHelper,
    ITestContextAccessor testContextAccessor,
    MongoVersion version,
    MongoEdition edition,
    bool useSingleNodeReplicaSet)
{
    // MongoDB v6 combinations
    public class MongoV6CommunityStandalone(ITestOutputHelper testOutputHelper, ITestContextAccessor testContextAccessor)
        : MongoRunnerImportExportTests(testOutputHelper, testContextAccessor, MongoVersion.V6, MongoEdition.Community, useSingleNodeReplicaSet: false);

    public class MongoV6CommunityReplicaSet(ITestOutputHelper testOutputHelper, ITestContextAccessor testContextAccessor)
        : MongoRunnerImportExportTests(testOutputHelper, testContextAccessor, MongoVersion.V6, MongoEdition.Community, useSingleNodeReplicaSet: true);

    public class MongoV6EnterpriseStandalone(ITestOutputHelper testOutputHelper, ITestContextAccessor testContextAccessor)
        : MongoRunnerImportExportTests(testOutputHelper, testContextAccessor, MongoVersion.V6, MongoEdition.Enterprise, useSingleNodeReplicaSet: false);

    public class MongoV6EnterpriseReplicaSet(ITestOutputHelper testOutputHelper, ITestContextAccessor testContextAccessor)
        : MongoRunnerImportExportTests(testOutputHelper, testContextAccessor, MongoVersion.V6, MongoEdition.Enterprise, useSingleNodeReplicaSet: true);

    // MongoDB v7 combinations
    public class MongoV7CommunityStandalone(ITestOutputHelper testOutputHelper, ITestContextAccessor testContextAccessor)
        : MongoRunnerImportExportTests(testOutputHelper, testContextAccessor, MongoVersion.V7, MongoEdition.Community, useSingleNodeReplicaSet: false);

    public class MongoV7CommunityReplicaSet(ITestOutputHelper testOutputHelper, ITestContextAccessor testContextAccessor)
        : MongoRunnerImportExportTests(testOutputHelper, testContextAccessor, MongoVersion.V7, MongoEdition.Community, useSingleNodeReplicaSet: true);

    public class MongoV7EnterpriseStandalone(ITestOutputHelper testOutputHelper, ITestContextAccessor testContextAccessor)
        : MongoRunnerImportExportTests(testOutputHelper, testContextAccessor, MongoVersion.V7, MongoEdition.Enterprise, useSingleNodeReplicaSet: false);

    public class MongoV7EnterpriseReplicaSet(ITestOutputHelper testOutputHelper, ITestContextAccessor testContextAccessor)
        : MongoRunnerImportExportTests(testOutputHelper, testContextAccessor, MongoVersion.V7, MongoEdition.Enterprise, useSingleNodeReplicaSet: true);

    // MongoDB v8 combinations
    public class MongoV8CommunityStandalone(ITestOutputHelper testOutputHelper, ITestContextAccessor testContextAccessor)
        : MongoRunnerImportExportTests(testOutputHelper, testContextAccessor, MongoVersion.V8, MongoEdition.Community, useSingleNodeReplicaSet: false);

    public class MongoV8CommunityReplicaSet(ITestOutputHelper testOutputHelper, ITestContextAccessor testContextAccessor)
        : MongoRunnerImportExportTests(testOutputHelper, testContextAccessor, MongoVersion.V8, MongoEdition.Community, useSingleNodeReplicaSet: true);

    public class MongoV8EnterpriseStandalone(ITestOutputHelper testOutputHelper, ITestContextAccessor testContextAccessor)
        : MongoRunnerImportExportTests(testOutputHelper, testContextAccessor, MongoVersion.V8, MongoEdition.Enterprise, useSingleNodeReplicaSet: false);

    public class MongoV8EnterpriseReplicaSet(ITestOutputHelper testOutputHelper, ITestContextAccessor testContextAccessor)
        : MongoRunnerImportExportTests(testOutputHelper, testContextAccessor, MongoVersion.V8, MongoEdition.Enterprise, useSingleNodeReplicaSet: true);

    [Fact]
    public async Task RunAsync()
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
            UseSingleNodeReplicaSet = useSingleNodeReplicaSet,
            StandardOutputLogger = line => MongoTrace.LogToOutput(line, testOutputHelper),
            StandardErrorLogger = line => MongoTrace.LogToOutput(line, testOutputHelper),
            AdditionalArguments = edition == MongoEdition.Enterprise
                ? ["--quiet", "--storageEngine", "inMemory"]
                : ["--quiet"],
        };

        using (var runner = await MongoRunner.RunAsync(options, testContextAccessor.Current.CancellationToken))
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
}