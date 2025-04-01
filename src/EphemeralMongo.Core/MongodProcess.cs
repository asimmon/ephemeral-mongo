using System.Diagnostics;
using System.Globalization;
using MongoDB.Bson;
using MongoDB.Driver;
using MongoDB.Driver.Core.Events;
using MongoDB.Driver.Core.Servers;

namespace EphemeralMongo;

internal sealed class MongodProcess : BaseMongoProcess
{
    private const string ConnectionReadySentence = "waiting for connections";

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
        using var cts = new CancellationTokenSource();
        using var isReplicaSetReadyMre = new ManualResetEventSlim();
        using var isTransactionReadyMre = new ManualResetEventSlim();

        void OnClusterDescriptionChanged(ClusterDescriptionChangedEvent evt)
        {
            if (!isReplicaSetReadyMre.IsSet && evt.NewDescription.Servers.Any(x => x.Type == ServerType.ReplicaSetPrimary && x.State == ServerState.Connected))
            {
                isReplicaSetReadyMre.Set();
            }

            if (!isTransactionReadyMre.IsSet && evt.NewDescription.Servers.Any(x => x.State == ServerState.Connected && x.IsDataBearing))
            {
                isTransactionReadyMre.Set();
            }
        }

        try
        {
            var settings = new MongoClientSettings
            {
                Server = new MongoServerAddress("127.0.0.1", this.Options.MongoPort!.Value),
                ReplicaSetName = this.Options.ReplicaSetName,
                DirectConnection = true,
                ClusterConfigurator = builder =>
                {
                    builder.Subscribe<ClusterDescriptionChangedEvent>(OnClusterDescriptionChanged);
                }
            };

            var client = new MongoClient(settings);
            var admin = client.GetDatabase("admin");

            var replConfig = new BsonDocument(new List<BsonElement>
            {
                new BsonElement("_id", this.Options.ReplicaSetName),
                new BsonElement("members", new BsonArray
                {
                    new BsonDocument
                    {
                        { "_id", 0 },
                        { "host", string.Format(CultureInfo.InvariantCulture, "127.0.0.1:{0}", this.Options.MongoPort) }
                    },
                }),
            });

            var command = new BsonDocument("replSetInitiate", replConfig);
            cts.CancelAfter(this.Options.ReplicaSetSetupTimeout);

            try
            {
                admin.RunCommand<BsonDocument>(command, cancellationToken: cts.Token);
            }
            catch (OperationCanceledException) when (cts.IsCancellationRequested)
            {
                var timeoutMessage = string.Format(
                    CultureInfo.InvariantCulture,
                    "Replica set initialization command took longer than the specified timeout of {0} seconds. Consider increasing the value of '{1}'.",
                    this.Options.ReplicaSetSetupTimeout.TotalSeconds,
                    nameof(this.Options.ReplicaSetSetupTimeout));

                throw new TimeoutException(timeoutMessage);
            }

            try
            {
                isReplicaSetReadyMre.Wait(cts.Token);
            }
            catch (OperationCanceledException) when (cts.IsCancellationRequested)
            {
                var timeoutMessage = string.Format(
                    CultureInfo.InvariantCulture,
                    "Replica set initialization took longer than the specified timeout of {0} seconds. Consider increasing the value of '{1}'.",
                    this.Options.ReplicaSetSetupTimeout.TotalSeconds,
                    nameof(this.Options.ReplicaSetSetupTimeout));

                throw new TimeoutException(timeoutMessage);
            }

            try
            {
                isTransactionReadyMre.Wait(cts.Token);
            }
            catch (OperationCanceledException) when (cts.IsCancellationRequested)
            {
                var timeoutMessage = string.Format(
                    CultureInfo.InvariantCulture,
                    "Cluster readiness for transactions took longer than the specified timeout of {0} seconds. Consider increasing the value of '{1}'.",
                    this.Options.ReplicaSetSetupTimeout.TotalSeconds,
                    nameof(this.Options.ReplicaSetSetupTimeout));

                throw new TimeoutException(timeoutMessage);
            }
        }
        catch (TimeoutException)
        {
            throw;
        }
        catch (Exception ex)
        {
            this.Options.StandardErrorLogger?.Invoke("An error occurred while initializing the replica set: " + ex);
            throw;
        }
    }
}