namespace EphemeralMongo;

public sealed class MongoRunnerOptions
{
    private string? _dataDirectory;
    private string? _binaryDirectory;
    private TimeSpan _connectionTimeout = TimeSpan.FromSeconds(30);
    private TimeSpan _replicaSetSetupTimeout = TimeSpan.FromSeconds(10);

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

        this.AdditionalArguments = options.AdditionalArguments;
        this.UseSingleNodeReplicaSet = options.UseSingleNodeReplicaSet;
        this.StandardOuputLogger = options.StandardOuputLogger;
        this.StandardErrorLogger = options.StandardErrorLogger;
        this.ReplicaSetName = options.ReplicaSetName;
        this.MongoPort = options.MongoPort;
    }

    public string? DataDirectory
    {
        get => this._dataDirectory;
        set => this._dataDirectory = CheckDirectoryPathFormat(value) is { } ex ? throw new ArgumentException(nameof(this.DataDirectory), ex) : value;
    }

    public string? BinaryDirectory
    {
        get => this._binaryDirectory;
        set => this._binaryDirectory = CheckDirectoryPathFormat(value) is { } ex ? throw new ArgumentException(nameof(this.BinaryDirectory), ex) : value;
    }

    public string? AdditionalArguments { get; set; }

    public TimeSpan ConnectionTimeout
    {
        get => this._connectionTimeout;
        set => this._connectionTimeout = value >= TimeSpan.Zero ? value : throw new ArgumentOutOfRangeException(nameof(this.ConnectionTimeout));
    }

    public bool UseSingleNodeReplicaSet { get; set; }

    public TimeSpan ReplicaSetSetupTimeout
    {
        get => this._replicaSetSetupTimeout;
        set => this._replicaSetSetupTimeout = value >= TimeSpan.Zero ? value : throw new ArgumentOutOfRangeException(nameof(this.ReplicaSetSetupTimeout));
    }

    public Logger? StandardOuputLogger { get; set; }

    public Logger? StandardErrorLogger { get; set; }

    // Internal properties start here
    internal string ReplicaSetName { get; set; } = "singleNodeReplSet";

    public int? MongoPort { get; set; }

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