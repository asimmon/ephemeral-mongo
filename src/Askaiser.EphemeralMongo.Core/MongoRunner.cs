using System.Globalization;
using System.Runtime.ExceptionServices;
using System.Runtime.InteropServices;

namespace Askaiser.EphemeralMongo.Core;

public sealed class MongoRunner : IDisposable
{
    private readonly IFileSystem _fileSystem;
    private readonly IPortFactory _portFactory;
    private readonly IMongoExecutableLocator _executableLocator;
    private readonly IMongoProcessFactory _processFactory;

    private IMongoProcess? _process;
    private string? _dataDirectory;

    private MongoRunner(IFileSystem fileSystem, IPortFactory portFactory, IMongoExecutableLocator executableLocator, IMongoProcessFactory processFactory)
    {
        this._fileSystem = fileSystem;
        this._portFactory = portFactory;
        this._executableLocator = executableLocator;
        this._processFactory = processFactory;
    }

    public static IMongoRunner Run(MongoRunnerOptions? options = null)
    {
        var runner = new MongoRunner(new FileSystem(), new PortFactory(), new MongoExecutableLocator(), new MongoProcessFactory());
        return runner.RunInternal(options);
    }

    private IMongoRunner RunInternal(MongoRunnerOptions? options = null)
    {
        options ??= new MongoRunnerOptions();

        // Ensure data directory exists and has no existing MongoDB lock file
        this._dataDirectory = options.DataDirectory ?? Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());
        this._fileSystem.CreateDirectory(this._dataDirectory);

        // https://stackoverflow.com/a/6857973/825695
        var lockFilePath = Path.Combine(this._dataDirectory, "mongod.lock");
        this._fileSystem.DeleteFile(lockFilePath);

        // Find MongoDB and make it executable
        options.MongoExecutablePath = this._executableLocator.FindMongoExecutablePath() ?? throw new InvalidOperationException("Could not find MongoDB executable");
        this._fileSystem.MakeFileExecutable(options.MongoExecutablePath);

        options.MongoPort = this._portFactory.GetRandomAvailablePort();

        // Build MongoDB executable arguments
        options.MongoArguments = string.Format(CultureInfo.InvariantCulture, "--dbpath \"{0}\" --port {1} --bind_ip 127.0.0.1", this._dataDirectory, options.MongoPort);
        options.MongoArguments += RuntimeInformation.IsOSPlatform(OSPlatform.Windows) ? string.Empty : " --tlsMode disabled";
        options.MongoArguments += options.UseSingleNodeReplicaSet ? " --replSet " + options.ReplicaSetName : string.Empty;
        options.MongoArguments += options.AdditionalArguments == null ? string.Empty : " " + options.AdditionalArguments;

        this._process = this._processFactory.Create(options);
        this._process.Start();

        var connectionStringFormat = options.UseSingleNodeReplicaSet ? "mongodb://127.0.0.1:{0}/?connect=direct&replicaSet={1}&readPreference=primary" : "mongodb://127.0.0.1:{0}";
        var connectionString = string.Format(CultureInfo.InvariantCulture, connectionStringFormat, options.MongoPort, options.ReplicaSetName);

        return new MongoRunnerDisposer(this, connectionString);
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

    private sealed class MongoRunnerDisposer : IMongoRunner
    {
        private readonly MongoRunner _runner;
        private int _isDisposed;

        public MongoRunnerDisposer(MongoRunner runner, string connectionString)
        {
            this._runner = runner;
            this.ConnectionString = connectionString;
        }

        public string ConnectionString { get; }

        public void Dispose()
        {
            if (Interlocked.CompareExchange(ref this._isDisposed, 1, 0) == 0)
            {
                this._runner.Dispose();
            }
        }
    }
}