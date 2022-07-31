namespace EphemeralMongo.Core;

public sealed class MongoRunnerOptions
{
    private readonly string? _dataDirectory;
    private readonly string? _binaryDirectory;
    private readonly TimeSpan _connectionTimeout = TimeSpan.FromSeconds(30);
    private readonly TimeSpan _replicaSetSetupTimeout = TimeSpan.FromSeconds(10);

    public string? DataDirectory
    {
        get => this._dataDirectory;
        init => this._dataDirectory = CheckDirectoryPathFormat(value) is { } ex ? throw new ArgumentException(nameof(this.DataDirectory), ex) : value;
    }

    public string? BinaryDirectory
    {
        get => this._binaryDirectory;
        init => this._binaryDirectory = CheckDirectoryPathFormat(value) is { } ex ? throw new ArgumentException(nameof(this.BinaryDirectory), ex) : value;
    }

    public string? AdditionalArguments { get; init; }

    public TimeSpan ConnectionTimeout
    {
        get => this._connectionTimeout;
        init => this._connectionTimeout = value >= TimeSpan.Zero ? value : throw new ArgumentOutOfRangeException(nameof(this.ConnectionTimeout));
    }

    public bool UseSingleNodeReplicaSet { get; init; }

    public TimeSpan ReplicaSetSetupTimeout
    {
        get => this._replicaSetSetupTimeout;
        init => this._replicaSetSetupTimeout = value >= TimeSpan.Zero ? value : throw new ArgumentOutOfRangeException(nameof(this.ReplicaSetSetupTimeout));
    }

    public Logger? StandardOuputLogger { get; init; }

    public Logger? StandardErrorLogger { get; init; }

    // Internal properties start here
    internal string ReplicaSetName { get; set; } = "singleNodeReplSet";

    internal int MongoPort { get; set; }

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