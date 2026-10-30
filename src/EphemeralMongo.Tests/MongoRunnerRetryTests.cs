using System.ComponentModel;

namespace EphemeralMongo.Tests;

public class MongoRunnerRetryTests(ITestContextAccessor testContextAccessor)
{
    private const string ExecutablePath = "/fake/path/to/mongod";

    [Theory]
    [InlineData(ExceptionType.Win32, $"An error occurred trying to start process '{ExecutablePath}' with working directory '/tmp'. Text file busy", true)]
    [InlineData(ExceptionType.ProcessExit, $"The MongoDB process '{ExecutablePath}' exited unexpectedly with code 126. Output: sh: 1: exec: {ExecutablePath}: Text file busy", true)]
    [InlineData(ExceptionType.Win32, $"An error occurred trying to start process '{ExecutablePath}' with working directory '/tmp'. No such file or directory", false)]
    [InlineData(ExceptionType.ProcessExit, $"The MongoDB process '{ExecutablePath}' exited unexpectedly with code 48.", false)]
    [InlineData(ExceptionType.EphemeralMongo, "Text file busy", false)]
    [InlineData(ExceptionType.Other, "Text file busy", false)]
    public void IsTextFileBusy_Matches_Process_Start_And_Shell_Exec_Failures(ExceptionType exceptionType, string message, bool expected)
    {
        Exception exception = exceptionType switch
        {
            ExceptionType.Win32 => new Win32Exception(26, message),
            ExceptionType.ProcessExit => EphemeralMongoException.FromProcessExit(message, 126),
            ExceptionType.EphemeralMongo => new EphemeralMongoException(message),
            _ => new InvalidOperationException(message),
        };

        Assert.Equal(expected, MongoRunner.IsTextFileBusy(exception));
    }

    [Fact]
    public async Task RunAsync_Retries_With_New_Process_When_Text_File_Is_Busy()
    {
        var processFactory = new FakeProcessFactory(
            new Win32Exception(26, "Text file busy"),
            EphemeralMongoException.FromProcessExit("Output: sh: 1: exec: mongod: Text file busy", 126),
            null);
        var errors = new List<string>();

        using (await CreateRunner(processFactory, errors).RunInternalAsync(testContextAccessor.Current.CancellationToken))
        {
            Assert.Equal(3, processFactory.Processes.Count);
            Assert.True(processFactory.Processes[0].IsDisposed);
            Assert.True(processFactory.Processes[1].IsDisposed);
            Assert.False(processFactory.Processes[2].IsDisposed);
            Assert.Equal(2, errors.Count(x => x.Contains("busy")));
        }
    }

    [Fact]
    public async Task RunAsync_Throws_When_Text_File_Is_Still_Busy_After_Retries()
    {
        var lastException = EphemeralMongoException.FromProcessExit("Output: sh: 1: exec: mongod: Text file busy", 126);
        var processFactory = new FakeProcessFactory(new Win32Exception(26, "Text file busy"), new Win32Exception(26, "Text file busy"), lastException);

        var ex = await Assert.ThrowsAsync<EphemeralMongoException>(() => CreateRunner(processFactory, []).RunInternalAsync(testContextAccessor.Current.CancellationToken));

        Assert.Same(lastException, ex);
        Assert.Equal(3, processFactory.Processes.Count);
        Assert.All(processFactory.Processes, x => Assert.True(x.IsDisposed));
    }

    public enum ExceptionType
    {
        Win32,
        ProcessExit,
        EphemeralMongo,
        Other,
    }

    private static MongoRunner CreateRunner(FakeProcessFactory processFactory, List<string> errors)
    {
        var options = new MongoRunnerOptions
        {
            // A user-provided data directory and port skip the cleanup of old data directories and the random port selection
            DataDirectory = "data",
            MongoPort = 27017,
            StandardErrorLogger = errors.Add,
        };

        return new MongoRunner(new FakeFileSystem(), timeProvider: null!, portFactory: null!, new FakeExecutableLocator(), processFactory, options);
    }

    // Each process throws the next exception from StartAsync, or starts successfully for null
    private sealed class FakeProcessFactory(params Exception?[] startExceptions) : IMongoProcessFactory
    {
        public List<FakeProcess> Processes { get; } = [];

        public IMongoProcess CreateMongoProcess(MongoRunnerOptions options, MongoProcessKind processKind, string executablePath, string[] arguments)
        {
            var process = new FakeProcess(startExceptions[this.Processes.Count]);
            this.Processes.Add(process);
            return process;
        }
    }

    private sealed class FakeProcess(Exception? startException) : IMongoProcess
    {
        public bool IsDisposed { get; private set; }

        public Task StartAsync(CancellationToken cancellationToken) => startException == null ? Task.CompletedTask : Task.FromException(startException);

        public void Dispose() => this.IsDisposed = true;
    }

    private sealed class FakeExecutableLocator : IMongoExecutableLocator
    {
        public Task<string> FindMongoExecutablePathAsync(MongoRunnerOptions options, MongoProcessKind processKind, CancellationToken cancellationToken) => Task.FromResult(ExecutablePath);
    }

    private sealed class FakeFileSystem : IFileSystem
    {
        public void CreateDirectory(string path)
        {
        }

        public void DeleteDirectory(string path) => throw new NotSupportedException();

        public void DeleteFile(string path)
        {
        }

        public string[] GetDirectories(string path, string searchPattern, SearchOption searchOption) => throw new NotSupportedException();

        public DateTime GetDirectoryCreationTimeUtc(string path) => throw new NotSupportedException();

        public bool IsFileLocked(string path) => throw new NotSupportedException();

        public void MakeFileExecutable(string path)
        {
        }
    }
}
