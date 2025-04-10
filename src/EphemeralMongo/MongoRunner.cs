using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Runtime.ExceptionServices;
using System.Runtime.InteropServices;
using MongoDB.Bson;
using MongoDB.Driver;

namespace EphemeralMongo;

public sealed class MongoRunner
{
    private static readonly string DefaultRootDataDirectory = Path.Combine(Path.GetTempPath(), "ephemeral-mongo", "data");

    private readonly IFileSystem _fileSystem;
    private readonly ITimeProvider _timeProvider;
    private readonly IPortFactory _portFactory;
    private readonly IMongoExecutableLocator _executableLocator;
    private readonly IMongoProcessFactory _processFactory;
    private readonly MongoRunnerOptions _options;

    private IMongoProcess? _process;
    private string? _dataDirectory;

    private MongoRunner(IFileSystem fileSystem, ITimeProvider timeProvider, IPortFactory portFactory, IMongoExecutableLocator executableLocator, IMongoProcessFactory processFactory, MongoRunnerOptions? options = null)
    {
        this._fileSystem = fileSystem;
        this._timeProvider = timeProvider;
        this._portFactory = portFactory;
        this._executableLocator = executableLocator;
        this._processFactory = processFactory;
        this._options = options == null ? new MongoRunnerOptions() : new MongoRunnerOptions(options);
    }

    public static Task<IMongoRunner> RunAsync(MongoRunnerOptions? options = null, CancellationToken cancellationToken = default)
    {
        var runner = new MongoRunner(new FileSystem(), new TimeProvider(), new PortFactory(), new MongoExecutableLocator(), new MongoProcessFactory(), options);
        return runner.RunInternalAsync(cancellationToken);
    }

    public static IMongoRunner Run(MongoRunnerOptions? options = null)
    {
        return Task.Run(() => RunAsync(options)).GetAwaiter().GetResult();
    }

    private async Task<IMongoRunner> RunInternalAsync(CancellationToken cancellationToken)
    {
        try
        {
            // Find MongoDB and make it executable
            var executablePath = await this._executableLocator.FindMongoExecutablePathAsync(this._options, MongoProcessKind.Mongod, cancellationToken).ConfigureAwait(false);
            this._fileSystem.MakeFileExecutable(executablePath);

            // Ensure data directory exists...
            if (this._options.DataDirectory != null)
            {
                this._dataDirectory = this._options.DataDirectory;
            }
            else
            {
                this._options.RootDataDirectoryPath ??= DefaultRootDataDirectory;
                this._dataDirectory = Path.Combine(this._options.RootDataDirectoryPath, Path.GetRandomFileName());
            }

            this._fileSystem.CreateDirectory(this._dataDirectory);

            try
            {
                // ...and has no existing MongoDB lock file
                // https://stackoverflow.com/a/6857973/825695
                var lockFilePath = Path.Combine(this._dataDirectory, "mongod.lock");
                this._fileSystem.DeleteFile(lockFilePath);
            }
            catch
            {
                // Ignored - this data directory might already be in use, we'll see later how mongod reacts
            }

            this.CleanupOldDataDirectories();

            this._options.MongoPort ??= this._portFactory.GetRandomAvailablePort();

            // Build mongod executable arguments
            List<string> arguments =
            [
                "--dbpath", this._dataDirectory,
                "--port", this._options.MongoPort.Value.ToString(CultureInfo.InvariantCulture),
                "--bind_ip", "127.0.0.1"
            ];

            if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            {
                arguments.Add("--tlsMode");
                arguments.Add("disabled");
            }

            if (this._options.UseSingleNodeReplicaSet)
            {
                arguments.Add("--replSet");
                arguments.Add(this._options.ReplicaSetName);
            }

            if (this._options.AdditionalArguments != null)
            {
                foreach (var additionalArgument in this._options.AdditionalArguments)
                {
                    if (!string.IsNullOrWhiteSpace(additionalArgument))
                    {
                        arguments.Add(additionalArgument);
                    }
                }
            }

            this._process = this._processFactory.CreateMongoProcess(this._options, MongoProcessKind.Mongod, executablePath, [.. arguments]);
            await this._process.StartAsync(cancellationToken).ConfigureAwait(false);

            var connectionStringFormat = this._options.UseSingleNodeReplicaSet ? "mongodb://127.0.0.1:{0}/?directConnection=true&replicaSet={1}&readPreference=primary" : "mongodb://127.0.0.1:{0}";
            var connectionString = string.Format(CultureInfo.InvariantCulture, connectionStringFormat, this._options.MongoPort, this._options.ReplicaSetName);

            return new StartedMongoRunner(this, connectionString);
        }
        catch
        {
            this.Dispose(throwOnException: false);
            throw;
        }
    }

    private static int _ongoingRootDataDirectoryCleanupCount;

    private void CleanupOldDataDirectories()
    {
        if (this._options.DataDirectory != null)
        {
            // Data directory was set by user, do not trigger cleanup
            return;
        }

        try
        {
            var isCleanupOngoing = Interlocked.Increment(ref _ongoingRootDataDirectoryCleanupCount) > 1;
            if (isCleanupOngoing)
            {
                return;
            }

            string[] dataDirectoryPaths;
            try
            {
                dataDirectoryPaths = this._fileSystem.GetDirectories(this._options.RootDataDirectoryPath!, "*", SearchOption.TopDirectoryOnly);
            }
            catch (Exception ex)
            {
                this._options.StandardErrorLogger?.Invoke($"An error occurred while trying to enumerate existing data directories for cleanup in '{DefaultRootDataDirectory}': {ex.Message}");
                return;
            }

            foreach (var dataDirectoryPath in dataDirectoryPaths)
            {
                try
                {
                    var dataDirectoryAge = this._timeProvider.UtcNow - this._fileSystem.GetDirectoryCreationTimeUtc(dataDirectoryPath);
                    if (dataDirectoryAge >= this._options.DataDirectoryLifetime)
                    {
                        this._fileSystem.DeleteDirectory(dataDirectoryPath);
                    }
                }
                catch (Exception ex)
                {
                    this._options.StandardErrorLogger?.Invoke($"An error occurred while trying to delete old data directory '{dataDirectoryPath}': {ex.Message}");
                }
            }
        }
        finally
        {
            Interlocked.Decrement(ref _ongoingRootDataDirectoryCleanupCount);
        }
    }

    private void Dispose(bool throwOnException)
    {
        var exceptions = new List<Exception>(1);

        try
        {
            this._process?.Dispose();
        }
        catch (Exception ex)
        {
            exceptions.Add(ex);
        }

        try
        {
            // Do not dispose data directory if set from user input or the root data directory path was set for tests
            if (this._dataDirectory != null && this._options.DataDirectory == null && this._options.RootDataDirectoryPath == DefaultRootDataDirectory)
            {
                this._fileSystem.DeleteDirectory(this._dataDirectory);
            }
        }
        catch (Exception ex)
        {
            exceptions.Add(ex);
        }

        if (throwOnException)
        {
            if (exceptions.Count == 1)
            {
                ExceptionDispatchInfo.Capture(exceptions[0]).Throw();
            }
            else if (exceptions.Count > 1)
            {
                throw new AggregateException(exceptions);
            }
        }
    }

    private sealed class StartedMongoRunner : IMongoRunner
    {
        private readonly MongoRunner _runner;
        private int _isDisposed;

        public StartedMongoRunner(MongoRunner runner, string connectionString)
        {
            this._runner = runner;
            this.ConnectionString = connectionString;
        }

        public string ConnectionString { get; }

        [SuppressMessage("Maintainability", "CA1513", Justification = "ObjectDisposedException.ThrowIf isn't worth it")]
        public async Task ImportAsync(string database, string collection, string inputFilePath, string[]? additionalArguments = null, bool drop = false, CancellationToken cancellationToken = default)
        {
            if (Interlocked.CompareExchange(ref this._isDisposed, 0, 0) == 1)
            {
                throw new ObjectDisposedException("MongoDB runner is already disposed");
            }

            if (string.IsNullOrWhiteSpace(database))
            {
                throw new ArgumentException("Database name is required", nameof(database));
            }

            if (string.IsNullOrWhiteSpace(collection))
            {
                throw new ArgumentException("Collection name is required", nameof(collection));
            }

            if (string.IsNullOrWhiteSpace(inputFilePath))
            {
                throw new ArgumentException("Input file path is required", nameof(inputFilePath));
            }

            var executablePath = await this._runner._executableLocator.FindMongoExecutablePathAsync(this._runner._options, MongoProcessKind.MongoImport, cancellationToken).ConfigureAwait(false);
            this._runner._fileSystem.MakeFileExecutable(executablePath);

            List<string> arguments =
            [
                "--uri", this.ConnectionString,
                "--db", database,
                "--collection", collection,
                "--file", inputFilePath
            ];

            if (drop)
            {
                arguments.Add("--drop");
            }

            if (additionalArguments != null)
            {
                foreach (var additionalArgument in additionalArguments)
                {
                    if (!string.IsNullOrWhiteSpace(additionalArgument))
                    {
                        arguments.Add(additionalArgument);
                    }
                }
            }

            using (var process = this._runner._processFactory.CreateMongoProcess(this._runner._options, MongoProcessKind.MongoImport, executablePath, [.. arguments]))
            {
                await process.StartAsync(cancellationToken).ConfigureAwait(false);
            }
        }

        public void Import(string database, string collection, string inputFilePath, string[]? additionalArguments = null, bool drop = false)
        {
            Task.Run(() => this.ImportAsync(database, collection, inputFilePath, additionalArguments, drop)).GetAwaiter().GetResult();
        }

        [SuppressMessage("StyleCop.CSharp.ReadabilityRules", "SA1117:Parameters should be on same line or separate lines", Justification = "Would have used too many lines, and this way string.Format is still very readable")]
        [SuppressMessage("Maintainability", "CA1513", Justification = "ObjectDisposedException.ThrowIf isn't worth it")]
        public async Task ExportAsync(string database, string collection, string outputFilePath, string[]? additionalArguments = null, CancellationToken cancellationToken = default)
        {
            if (Interlocked.CompareExchange(ref this._isDisposed, 0, 0) == 1)
            {
                throw new ObjectDisposedException("MongoDB runner is already disposed");
            }

            if (string.IsNullOrWhiteSpace(database))
            {
                throw new ArgumentException("Database name is required", nameof(database));
            }

            if (string.IsNullOrWhiteSpace(collection))
            {
                throw new ArgumentException("Collection name is required", nameof(collection));
            }

            if (string.IsNullOrWhiteSpace(outputFilePath))
            {
                throw new ArgumentException("Output file path is required", nameof(outputFilePath));
            }

            var executablePath = await this._runner._executableLocator.FindMongoExecutablePathAsync(this._runner._options, MongoProcessKind.MongoExport, cancellationToken).ConfigureAwait(false);
            this._runner._fileSystem.MakeFileExecutable(executablePath);

            var arguments = new List<string>
            {
                "--uri", this.ConnectionString,
                "--db", database,
                "--collection", collection,
                "--out", outputFilePath
            };

            if (additionalArguments != null)
            {
                foreach (var additionalArgument in additionalArguments)
                {
                    if (!string.IsNullOrWhiteSpace(additionalArgument))
                    {
                        arguments.Add(additionalArgument);
                    }
                }
            }

            using (var process = this._runner._processFactory.CreateMongoProcess(this._runner._options, MongoProcessKind.MongoExport, executablePath, [.. arguments]))
            {
                await process.StartAsync(cancellationToken).ConfigureAwait(false);
            }
        }

        public void Export(string database, string collection, string outputFilePath, string[]? additionalArguments = null)
        {
            Task.Run(() => this.ExportAsync(database, collection, outputFilePath, additionalArguments)).GetAwaiter().GetResult();
        }

        public void Dispose()
        {
            if (Interlocked.CompareExchange(ref this._isDisposed, 1, 0) == 0)
            {
                this.TryShutdownQuietly();
                this._runner.Dispose(throwOnException: true);
            }
        }

        private void TryShutdownQuietly()
        {
            // https://www.mongodb.com/docs/v4.4/reference/command/shutdown/
            const int defaultShutdownTimeoutInSeconds = 10;

            var shutdownCommand = new BsonDocument
            {
                { "shutdown", 1 },
                { "force", true },
                { "timeoutSecs", defaultShutdownTimeoutInSeconds },
            };

            try
            {
                var client = new MongoClient(this.ConnectionString);
                var admin = client.GetDatabase("admin");

                using (var cts = new CancellationTokenSource(TimeSpan.FromSeconds(defaultShutdownTimeoutInSeconds)))
                {
                    admin.RunCommand<BsonDocument>(shutdownCommand, cancellationToken: cts.Token);
                }
            }
            catch (MongoConnectionException)
            {
                // This is the expected behavior as mongod is shutting down
            }
            catch
            {
                // Ignore other exceptions as well, we'll kill the process anyway
            }
        }
    }
}
