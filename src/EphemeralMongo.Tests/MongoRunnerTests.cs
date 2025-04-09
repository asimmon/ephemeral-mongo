namespace EphemeralMongo.Tests;

public class MongoRunnerTests(ITestOutputHelper testOutputHelper, ITestContextAccessor testContextAccessor)
{
    [Fact]
    public async Task Run_Fails_When_BinaryDirectory_Does_Not_Exist()
    {
        var options = new MongoRunnerOptions
        {
            StandardOutputLogger = line => MongoTrace.LogToOutput(line, testOutputHelper),
            StandardErrorLogger = line => MongoTrace.LogToOutput(line, testOutputHelper),
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
        var rootDataDirectoryPath = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());

        try
        {
            var options = new MongoRunnerOptions
            {
                StandardOutputLogger = line => MongoTrace.LogToOutput(line, testOutputHelper),
                StandardErrorLogger = line => MongoTrace.LogToOutput(line, testOutputHelper),
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
}