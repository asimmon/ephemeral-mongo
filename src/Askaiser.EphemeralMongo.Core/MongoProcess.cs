using System.Diagnostics;
using System.Globalization;
using MongoDB.Bson;
using MongoDB.Driver;
using MongoDB.Driver.Core.Servers;

namespace Askaiser.EphemeralMongo.Core;

internal sealed class MongoProcess : IMongoProcess
{
    private const string ConnectionReadySentence = "waiting for connections";
    private const string ReplicaSetReadySentence = "transition to primary complete; database writes are now permitted";

    private readonly MongoRunnerOptions _options;
    private readonly Process _process;

    public MongoProcess(MongoRunnerOptions options)
    {
        this._options = options;

        var processStartInfo = new ProcessStartInfo
        {
            FileName = options.MongoExecutablePath,
            Arguments = options.MongoArguments,
            CreateNoWindow = true,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };

        this._process = new Process
        {
            StartInfo = processStartInfo,
        };

        this._process.OutputDataReceived += this.OnOutputDataReceivedForLogging;
        this._process.ErrorDataReceived += this.OnErrorDataReceivedForLogging;
    }

    private void OnOutputDataReceivedForLogging(object sender, DataReceivedEventArgs args)
    {
        if (this._options.StandardOuputLogger != null && args.Data != null)
        {
            this._options.StandardOuputLogger(args.Data);
        }
    }

    private void OnErrorDataReceivedForLogging(object sender, DataReceivedEventArgs args)
    {
        if (this._options.StandardErrorLogger != null && args.Data != null)
        {
            this._options.StandardErrorLogger(args.Data);
        }
    }

    public void Start()
    {
        this.StartAndWaitForConnectionReadiness();

        if (this._options.UseSingleNodeReplicaSet)
        {
            this.ConfigureAndWaitForReplicaSetReadiness();
        }
    }

    private void StartAndWaitForConnectionReadiness()
    {
        using var isReadyToAcceptConnectionsMre = new ManualResetEventSlim();

        void OnOutputDataReceivedForConnectionReadiness(object sender, DataReceivedEventArgs args)
        {
            var isReadyToAcceptConnections = args.Data != null && args.Data.IndexOf(ConnectionReadySentence, StringComparison.OrdinalIgnoreCase) >= 0;
            if (isReadyToAcceptConnections && !isReadyToAcceptConnectionsMre.IsSet)
            {
                isReadyToAcceptConnectionsMre.Set();
            }
        }

        this._process.OutputDataReceived += OnOutputDataReceivedForConnectionReadiness;

        try
        {
            this._process.Start();

            this._process.BeginOutputReadLine();
            this._process.BeginErrorReadLine();

            var isReadyToAcceptConnections = isReadyToAcceptConnectionsMre.Wait(this._options.ConnectionTimeout);
            if (!isReadyToAcceptConnections)
            {
                var timeoutMessage = string.Format(
                    CultureInfo.InvariantCulture,
                    "MongoDB connection availability took longer than the specified timeout of {0} seconds. Consider increasing the value of '{1}'.",
                    this._options.ConnectionTimeout.TotalSeconds,
                    nameof(this._options.ConnectionTimeout));

                throw new TimeoutException(timeoutMessage);
            }
        }
        finally
        {
            this._process.OutputDataReceived -= OnOutputDataReceivedForConnectionReadiness;
        }
    }

    private void ConfigureAndWaitForReplicaSetReadiness()
    {
        using var isReplicaSetReadyMre = new ManualResetEventSlim();

        void OnOutputDataReceivedForReplicaSetReadiness(object sender, DataReceivedEventArgs args)
        {
            var isReplicaSetReady = args.Data != null && args.Data.IndexOf(ReplicaSetReadySentence, StringComparison.OrdinalIgnoreCase) >= 0;
            if (isReplicaSetReady && !isReplicaSetReadyMre.IsSet)
            {
                isReplicaSetReadyMre.Set();
            }
        }

        this._process.OutputDataReceived += OnOutputDataReceivedForReplicaSetReadiness;

        try
        {
            var connectionString = string.Format(CultureInfo.InvariantCulture, "mongodb://127.0.0.1:{0}/?connect=direct&replicaSet={1}", this._options.MongoPort, this._options.ReplicaSetName);

            var client = new MongoClient(connectionString);
            var admin = client.GetDatabase("admin");

            var replConfig = new BsonDocument(new List<BsonElement>
            {
                new BsonElement("_id", this._options.ReplicaSetName),
                new BsonElement("members", new BsonArray
                {
                    new BsonDocument { { "_id", 0 }, { "host", string.Format(CultureInfo.InvariantCulture, "127.0.0.1:{0}", this._options.MongoPort) } },
                }),
            });

            var command = new BsonDocument("replSetInitiate", replConfig);
            admin.RunCommand<BsonDocument>(command);

            var isReplicaSetReady = isReplicaSetReadyMre.Wait(this._options.ReplicaSetSetupTimeout);
            if (!isReplicaSetReady)
            {
                var timeoutMessage = string.Format(
                    CultureInfo.InvariantCulture,
                    "Replica set initialization took longer than the specified timeout of {0} seconds. Consider increasing the value of '{1}'.",
                    this._options.ReplicaSetSetupTimeout.TotalSeconds,
                    nameof(this._options.ReplicaSetSetupTimeout));

                throw new TimeoutException(timeoutMessage);
            }

            var isTransactionReady = SpinWait.SpinUntil(
                () => client.Cluster.Description.Servers.Any(x => x.State == ServerState.Connected && x.IsDataBearing),
                this._options.ReplicaSetSetupTimeout);

            if (!isTransactionReady)
            {
                var timeoutMessage = string.Format(
                    CultureInfo.InvariantCulture,
                    "Cluster readiness for transactions took longer than the specified timeout of {0} seconds. Consider increasing the value of '{1}'.",
                    this._options.ReplicaSetSetupTimeout.TotalSeconds,
                    nameof(this._options.ReplicaSetSetupTimeout));

                throw new TimeoutException(timeoutMessage);
            }
        }
        finally
        {
            this._process.OutputDataReceived -= OnOutputDataReceivedForReplicaSetReadiness;
        }
    }

    public void Dispose()
    {
        this._process.OutputDataReceived -= this.OnOutputDataReceivedForLogging;
        this._process.ErrorDataReceived -= this.OnErrorDataReceivedForLogging;

        this._process.CancelOutputRead();
        this._process.CancelErrorRead();

        if (!this._process.HasExited)
        {
            try
            {
                this._process.Kill();
                this._process.WaitForExit();
            }
            catch
            {
                // ignored, we did our best to stop the process
            }
        }

        this._process.Dispose();
    }
}