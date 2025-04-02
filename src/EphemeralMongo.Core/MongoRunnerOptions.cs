namespace EphemeralMongo;

public sealed class MongoRunnerOptions
{
    private string? _dataDirectory;
    private string? _binaryDirectory;
    private TimeSpan _connectionTimeout = TimeSpan.FromSeconds(30);
    private TimeSpan _replicaSetSetupTimeout = TimeSpan.FromSeconds(10);
    private int? _mongoPort;
    private TimeSpan _dataDirectoryLifetime = TimeSpan.FromHours(12);

    public MongoRunnerOptions()
    {
    }

    public MongoRunnerOptions(MongoRunnerOptions options)
    {
        if (options == null)
        {
            throw new ArgumentNullException(nameof(options));
        }

        this._dataDirectory = options._dataDirectory;
        this._binaryDirectory = options._binaryDirectory;
        this._connectionTimeout = options._connectionTimeout;
        this._replicaSetSetupTimeout = options._replicaSetSetupTimeout;
        this._mongoPort = options._mongoPort;
        this._dataDirectoryLifetime = options._dataDirectoryLifetime;

        this.AdditionalArguments = options.AdditionalArguments;
        this.UseSingleNodeReplicaSet = options.UseSingleNodeReplicaSet;
        this.StandardOutputLogger = options.StandardOutputLogger;
        this.StandardErrorLogger = options.StandardErrorLogger;
        this.ReplicaSetName = options.ReplicaSetName;
        this.RootDataDirectoryPath = options.RootDataDirectoryPath;
    }

    /// <summary>
    /// The directory where the mongod instance stores its data. If not specified, a temporary directory will be used.
    /// </summary>
    /// <exception cref="ArgumentException">The path is invalid.</exception>
    /// <seealso href="https://www.mongodb.com/docs/manual/reference/program/mongod/#std-option-mongod.--dbpath"/>
    public string? DataDirectory
    {
        get => this._dataDirectory;
        set => this._dataDirectory = CheckDirectoryPathFormat(value) is { } ex ? throw new ArgumentException(nameof(this.DataDirectory), ex) : value;
    }

    /// <summary>
    /// The directory where your own MongoDB binaries can be found (mongod, mongoexport and mongoimport).
    /// </summary>
    /// <exception cref="ArgumentException">The path is invalid.</exception>
    public string? BinaryDirectory
    {
        get => this._binaryDirectory;
        set => this._binaryDirectory = CheckDirectoryPathFormat(value) is { } ex ? throw new ArgumentException(nameof(this.BinaryDirectory), ex) : value;
    }

    /// <summary>
    /// Additional mongod CLI arguments.
    /// </summary>
    /// <seealso href="https://www.mongodb.com/docs/manual/reference/program/mongod/#options"/>
    public string? AdditionalArguments { get; set; }

    /// <summary>
    /// Maximum timespan to wait for mongod process to be ready to accept connections.
    /// </summary>
    /// <exception cref="ArgumentOutOfRangeException">The timeout cannot be negative.</exception>
    public TimeSpan ConnectionTimeout
    {
        get => this._connectionTimeout;
        set => this._connectionTimeout = value >= TimeSpan.Zero ? value : throw new ArgumentOutOfRangeException(nameof(this.ConnectionTimeout));
    }

    /// <summary>
    /// Whether to create a single node replica set or use a standalone mongod instance.
    /// </summary>
    public bool UseSingleNodeReplicaSet { get; set; }

    /// <summary>
    /// Maximum timespan to wait for the replica set to accept database writes.
    /// </summary>
    /// <exception cref="ArgumentOutOfRangeException">The timeout cannot be negative.</exception>
    public TimeSpan ReplicaSetSetupTimeout
    {
        get => this._replicaSetSetupTimeout;
        set => this._replicaSetSetupTimeout = value >= TimeSpan.Zero ? value : throw new ArgumentOutOfRangeException(nameof(this.ReplicaSetSetupTimeout));
    }

    /// <summary>
    /// A delegate that provides access to any MongodDB-related process standard output.
    /// </summary>
    [Obsolete("This property is deprecated and will be removed in a future version. Use StandardOutputLogger instead.")]
    public Logger? StandardOuputLogger
    {
        get => this.StandardOutputLogger;
        set => this.StandardOutputLogger = value;
    }

    /// <summary>
    /// A delegate that provides access to any MongodDB-related process standard output.
    /// </summary>
    public Logger? StandardOutputLogger { get; set; }

    /// <summary>
    /// A delegate that provides access to any MongodDB-related process error output.
    /// </summary>
    public Logger? StandardErrorLogger { get; set; }

    /// <summary>
    /// The mongod port to use. If not specified, a random available port will be used.
    /// </summary>
    /// <exception cref="ArgumentOutOfRangeException">The port must be greater than zero.</exception>
    public int? MongoPort
    {
        get => this._mongoPort;
        set => this._mongoPort = value is not <= 0 ? value : throw new ArgumentOutOfRangeException(nameof(this.MongoPort));
    }

    /// <summary>
    /// Ensures that all MongoDB child processes are killed when the current process is prematurely killed,
    /// for instance when killed from the task manager or the IDE unit tests window.
    /// Only works on Windows and modern .NET (netcoreapp3.1, net5.0, net6.0, net7.0 and so on):
    /// </summary>
    [Obsolete("This feature is now always enabled on Windows. This property will be removed in a future version.")]
    public bool KillMongoProcessesWhenCurrentProcessExits { get; set; }

    /// <summary>
    /// The lifetime of data directories that are automatically created when they are not specified by the user.
    /// When their age exceeds this value, they will be deleted on the next run of MongoRunner.
    /// </summary>
    /// <exception cref="ArgumentOutOfRangeException">The lifetime cannot be negative.</exception>
    public TimeSpan? DataDirectoryLifetime
    {
        get => this._dataDirectoryLifetime;
        set => this._dataDirectoryLifetime = value is not null && value >= TimeSpan.Zero ? value.Value : throw new ArgumentOutOfRangeException(nameof(this.DataDirectoryLifetime));
    }

    // Internal properties start here
    internal string ReplicaSetName { get; set; } = "singleNodeReplSet";

    // Useful for testing data directories cleanup
    internal string? RootDataDirectoryPath { get; set; }

    private static Exception? CheckDirectoryPathFormat(string? path)
    {
        if (path == null)
        {
            return new ArgumentNullException(nameof(path));
        }

        try
        {
            _ = new DirectoryInfo(path);
        }
        catch (Exception ex)
        {
            return ex;
        }

        return null;
    }
}