using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Runtime.ExceptionServices;
using System.Runtime.InteropServices;

namespace EphemeralMongo.Core;

public sealed class MongoRunner : IDisposable
{
    private readonly IFileSystem _fileSystem;
    private readonly IPortFactory _portFactory;
    private readonly IMongoExecutableLocator _executableLocator;
    private readonly IMongoProcessFactory _processFactory;
    private readonly MongoRunnerOptions _options;

    private IMongoProcess? _process;
    private string? _dataDirectory;

    private MongoRunner(IFileSystem fileSystem, IPortFactory portFactory, IMongoExecutableLocator executableLocator, IMongoProcessFactory processFactory, MongoRunnerOptions options)
    {
        this._fileSystem = fileSystem;
        this._portFactory = portFactory;
        this._executableLocator = executableLocator;
        this._processFactory = processFactory;
        this._options = options;
    }

    public static IMongoRunner Run(MongoRunnerOptions? options = null)
    {
        var runner = new MongoRunner(new FileSystem(), new PortFactory(), new MongoExecutableLocator(), new MongoProcessFactory(), options ?? new MongoRunnerOptions());
        return runner.RunInternal();
    }

    private IMongoRunner RunInternal()
    {
        // Ensure data directory exists and has no existing MongoDB lock file
        this._dataDirectory = this._options.DataDirectory ?? Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());
        this._fileSystem.CreateDirectory(this._dataDirectory);

        // https://stackoverflow.com/a/6857973/825695
        var lockFilePath = Path.Combine(this._dataDirectory, "mongod.lock");
        this._fileSystem.DeleteFile(lockFilePath);

        // Find MongoDB and make it executable
        var executablePath = this._executableLocator.FindMongoExecutablePath(this._options, MongoProcessKind.Mongod);
        this._fileSystem.MakeFileExecutable(executablePath);

        this._options.MongoPort = this._portFactory.GetRandomAvailablePort();

        // Build MongoDB executable arguments
        var arguments = string.Format(CultureInfo.InvariantCulture, "--dbpath \"{0}\" --port {1} --bind_ip 127.0.0.1", this._dataDirectory, this._options.MongoPort);
        arguments += RuntimeInformation.IsOSPlatform(OSPlatform.Windows) ? string.Empty : " --tlsMode disabled";
        arguments += this._options.UseSingleNodeReplicaSet ? " --replSet " + this._options.ReplicaSetName : string.Empty;
        arguments += this._options.AdditionalArguments == null ? string.Empty : " " + this._options.AdditionalArguments;

        this._process = this._processFactory.CreateMongoProcess(this._options, MongoProcessKind.Mongod, executablePath, arguments);
        this._process.Start();

        var connectionStringFormat = this._options.UseSingleNodeReplicaSet ? "mongodb://127.0.0.1:{0}/?connect=direct&replicaSet={1}&readPreference=primary" : "mongodb://127.0.0.1:{0}";
        var connectionString = string.Format(CultureInfo.InvariantCulture, connectionStringFormat, this._options.MongoPort, this._options.ReplicaSetName);

        return new StartedMongoRunner(this, connectionString);
    }

    public void Dispose()
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
            if (this._dataDirectory != null)
            {
                this._fileSystem.DeleteDirectory(this._dataDirectory);
            }
        }
        catch (Exception ex)
        {
            exceptions.Add(ex);
        }

        if (exceptions.Count == 1)
        {
            ExceptionDispatchInfo.Capture(exceptions[0]).Throw();
        }
        else if (exceptions.Count > 1)
        {
            throw new AggregateException(exceptions);
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

        [SuppressMessage("StyleCop.CSharp.ReadabilityRules", "SA1117:Parameters should be on same line or separate lines", Justification = "Would have used too many lines, and this way string.Format is still very readable")]
        public void Import(string database, string collection, string inputFilePath, string? additionalArguments = null, bool drop = false)
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

            var executablePath = this._runner._executableLocator.FindMongoExecutablePath(this._runner._options, MongoProcessKind.MongoImport);

            var arguments = string.Format(
                CultureInfo.InvariantCulture,
                @"--uri=""{0}"" --db={1} --collection={2} --file=""{3}"" {4} {5}",
                this.ConnectionString, database, collection, inputFilePath, drop ? " --drop" : string.Empty, additionalArguments ?? string.Empty);

            using (var process = this._runner._processFactory.CreateMongoProcess(this._runner._options, MongoProcessKind.MongoImport, executablePath, arguments))
            {
                process.Start();
            }
        }

        [SuppressMessage("StyleCop.CSharp.ReadabilityRules", "SA1117:Parameters should be on same line or separate lines", Justification = "Would have used too many lines, and this way string.Format is still very readable")]
        public void Export(string database, string collection, string outputFilePath, string? additionalArguments = null)
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

            var executablePath = this._runner._executableLocator.FindMongoExecutablePath(this._runner._options, MongoProcessKind.MongoExport);

            var arguments = string.Format(
                CultureInfo.InvariantCulture,
                @"--uri=""{0}"" --db={1} --collection={2} --out=""{3}"" {4}",
                this.ConnectionString, database, collection, outputFilePath, additionalArguments ?? string.Empty);

            using (var process = this._runner._processFactory.CreateMongoProcess(this._runner._options, MongoProcessKind.MongoExport, executablePath, arguments))
            {
                process.Start();
            }
        }

        public void Dispose()
        {
            if (Interlocked.CompareExchange(ref this._isDisposed, 1, 0) == 0)
            {
                this._runner.Dispose();
            }
        }
    }
}