using System.Diagnostics;
using System.Globalization;
using MongoDB.Bson;
using MongoDB.Driver;
using MongoDB.Driver.Core.Servers;

namespace EphemeralMongo;

internal sealed class MongodProcess : BaseMongoProcess
{
    private const string ConnectionReadySentence = "waiting for connections";
    private const string ReplicaSetReadySentence = "transition to primary complete; database writes are now permitted";

    public MongodProcess(MongoRunnerOptions options, string executablePath, string arguments)
        : base(options, executablePath, arguments)
    {
    }

    public override void Start()
    {
        this.StartAndWaitForConnectionReadiness();

        if (this.Options.UseSingleNodeReplicaSet)
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

        this.Process.OutputDataReceived += OnOutputDataReceivedForConnectionReadiness;

        try
        {
            this.Process.Start();

            this.Process.BeginOutputReadLine();
            this.Process.BeginErrorReadLine();

            var isReadyToAcceptConnections = isReadyToAcceptConnectionsMre.Wait(this.Options.ConnectionTimeout);
            if (!isReadyToAcceptConnections)
            {
                var timeoutMessage = string.Format(
                    CultureInfo.InvariantCulture,
                    "MongoDB connection availability took longer than the specified timeout of {0} seconds. Consider increasing the value of '{1}'.",
                    this.Options.ConnectionTimeout.TotalSeconds,
                    nameof(this.Options.ConnectionTimeout));

                throw new TimeoutException(timeoutMessage);
            }
        }
        finally
        {
            this.Process.OutputDataReceived -= OnOutputDataReceivedForConnectionReadiness;
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

        this.Process.OutputDataReceived += OnOutputDataReceivedForReplicaSetReadiness;

        try
        {
            var connectionString = string.Format(CultureInfo.InvariantCulture, "mongodb://127.0.0.1:{0}/?connect=direct&replicaSet={1}", this.Options.MongoPort, this.Options.ReplicaSetName);

            var client = new MongoClient(connectionString);
            var admin = client.GetDatabase("admin");

            var replConfig = new BsonDocument(new List<BsonElement>
            {
                new BsonElement("_id", this.Options.ReplicaSetName),
                new BsonElement("members", new BsonArray
                {
                    new BsonDocument { { "_id", 0 }, { "host", string.Format(CultureInfo.InvariantCulture, "127.0.0.1:{0}", this.Options.MongoPort) } },
                }),
            });

            var command = new BsonDocument("replSetInitiate", replConfig);
            admin.RunCommand<BsonDocument>(command);

            var isReplicaSetReady = isReplicaSetReadyMre.Wait(this.Options.ReplicaSetSetupTimeout);
            if (!isReplicaSetReady)
            {
                var timeoutMessage = string.Format(
                    CultureInfo.InvariantCulture,
                    "Replica set initialization took longer than the specified timeout of {0} seconds. Consider increasing the value of '{1}'.",
                    this.Options.ReplicaSetSetupTimeout.TotalSeconds,
                    nameof(this.Options.ReplicaSetSetupTimeout));

                throw new TimeoutException(timeoutMessage);
            }

            var isTransactionReady = SpinWait.SpinUntil(
                () => client.Cluster.Description.Servers.Any(x => x.State == ServerState.Connected && x.IsDataBearing),
                this.Options.ReplicaSetSetupTimeout);

            if (!isTransactionReady)
            {
                var timeoutMessage = string.Format(
                    CultureInfo.InvariantCulture,
                    "Cluster readiness for transactions took longer than the specified timeout of {0} seconds. Consider increasing the value of '{1}'.",
                    this.Options.ReplicaSetSetupTimeout.TotalSeconds,
                    nameof(this.Options.ReplicaSetSetupTimeout));

                throw new TimeoutException(timeoutMessage);
            }
        }
        finally
        {
            this.Process.OutputDataReceived -= OnOutputDataReceivedForReplicaSetReadiness;
        }
    }
}