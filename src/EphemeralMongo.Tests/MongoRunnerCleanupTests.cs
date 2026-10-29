namespace EphemeralMongo.Tests;

public class MongoRunnerCleanupTests
{
    // What .NET returns for the creation time of a directory that doesn't exist
    private static readonly DateTime MissingDirectoryCreationTime = DateTime.FromFileTimeUtc(0);

    [Fact]
    public void CleanupOldDataDirectories_Ignores_Directory_Deleted_Since_It_Was_Enumerated()
    {
        // Another runner deleted its own data directory between the enumeration and the deletion
        var fileSystem = new FakeFileSystem(new DirectoryNotFoundException("Could not find a part of the path"));
        var errors = new List<string>();

        CreateRunner(fileSystem, errors).CleanupOldDataDirectories();

        Assert.Empty(errors);
    }

    [Fact]
    public void CleanupOldDataDirectories_Logs_Other_Deletion_Errors()
    {
        var fileSystem = new FakeFileSystem(new UnauthorizedAccessException("Access denied"));
        var errors = new List<string>();

        CreateRunner(fileSystem, errors).CleanupOldDataDirectories();

        // Skipped when another runner's cleanup is ongoing in this process, as only one cleanup runs at a time
        if (fileSystem.DeleteAttempted)
        {
            var error = Assert.Single(errors);
            Assert.Contains("Access denied", error);
        }
    }

    private static MongoRunner CreateRunner(IFileSystem fileSystem, List<string> errors)
    {
        var options = new MongoRunnerOptions
        {
            RootDataDirectoryPath = FakeFileSystem.RootDataDirectoryPath,
            StandardErrorLogger = errors.Add,
        };

        return new MongoRunner(fileSystem, new FakeTimeProvider(), portFactory: null!, executableLocator: null!, processFactory: null!, options);
    }

    private sealed class FakeFileSystem(Exception deleteException) : IFileSystem
    {
        public const string RootDataDirectoryPath = "root";

        private static readonly string DataDirectoryPath = Path.Combine(RootDataDirectoryPath, "data");

        public bool DeleteAttempted { get; private set; }

        public string[] GetDirectories(string path, string searchPattern, SearchOption searchOption) => [DataDirectoryPath];

        public DateTime GetDirectoryCreationTimeUtc(string path) => MissingDirectoryCreationTime;

        public bool IsFileLocked(string path) => false;

        public void DeleteDirectory(string path)
        {
            this.DeleteAttempted = true;
            throw deleteException;
        }

        public void CreateDirectory(string path) => throw new NotSupportedException();

        public void DeleteFile(string path) => throw new NotSupportedException();

        public void MakeFileExecutable(string path) => throw new NotSupportedException();
    }

    private sealed class FakeTimeProvider : ITimeProvider
    {
        public DateTimeOffset UtcNow => DateTimeOffset.UtcNow;
    }
}
