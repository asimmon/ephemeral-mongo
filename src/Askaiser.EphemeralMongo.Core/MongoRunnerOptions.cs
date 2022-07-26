namespace Askaiser.EphemeralMongo.Core;

public sealed class MongoRunnerOptions
{
    private readonly string? _dataDirectory;
    private readonly TimeSpan _connectionTimeout = TimeSpan.FromSeconds(30);
    private readonly TimeSpan _replicaSetSetupTimeout = TimeSpan.FromSeconds(10);

    public string? DataDirectory
    {
        get => this._dataDirectory;
        init => this._dataDirectory = CheckPathFormat(value) is { } ex ? throw new ArgumentException(nameof(this.DataDirectory), ex) : value;
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

    internal string MongoExecutablePath { get; set; } = string.Empty;

    internal string MongoArguments { get; set; } = string.Empty;

    internal int MongoPort { get; set; }

    private static Exception? CheckPathFormat(string? path)
    {
        if (path == null)
        {
            return new ArgumentNullException(nameof(path));
        }

        try
        {
            _ = new FileInfo(path);
        }
        catch (Exception ex)
        {
            return ex;
        }

        return null;
    }
}