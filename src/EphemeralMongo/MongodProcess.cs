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

    public MongodProcess(MongoRunnerOptions options, string executablePath, string[] arguments)
        : base(options, executablePath, arguments)
    {
    }

    public override async Task StartAsync(CancellationToken cancellationToken)
    {
        await this.StartAndWaitForConnectionReadinessAsync(cancellationToken).ConfigureAwait(false);

        if (this.Options.UseSingleNodeReplicaSet)
        {
            await this.ConfigureAndWaitForReplicaSetReadinessAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    private async Task StartAndWaitForConnectionReadinessAsync(CancellationToken cancellationToken)
    {
        var isReadyToAcceptConnectionsTcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);

        void OnOutputDataReceivedForConnectionReadiness(object sender, DataReceivedEventArgs args)
        {
            var isReadyToAcceptConnections = args.Data != null && args.Data.IndexOf(ConnectionReadySentence, StringComparison.OrdinalIgnoreCase) >= 0;
            if (isReadyToAcceptConnections)
            {
                isReadyToAcceptConnectionsTcs.TrySetResult(true);
            }
        }

        void OnProcessExited(object sender, EventArgs e)
        {
            var exception = this.Process.ExitCode == 0
                ? new InvalidOperationException($"The process MongoDB process {this.Process.Id} exited unexpectedly.")
                : new InvalidOperationException($"The process MongoDB process {this.Process.Id} exited unexpectedly with code {this.Process.ExitCode}.");

            isReadyToAcceptConnectionsTcs.TrySetException(exception);
        }

        this.Process.OutputDataReceived += OnOutputDataReceivedForConnectionReadiness;
        this.Process.Exited += OnProcessExited;

        try
        {
            this.Process.Start();

            this.Process.BeginOutputReadLine();
            this.Process.BeginErrorReadLine();

            using (var timeoutCts = new CancellationTokenSource(this.Options.ConnectionTimeout))
            using (var combinedCts = CancellationTokenSource.CreateLinkedTokenSource(timeoutCts.Token, cancellationToken))
            using (combinedCts.Token.Register(() => isReadyToAcceptConnectionsTcs.TrySetCanceled(combinedCts.Token)))
            {
                try
                {
                    await isReadyToAcceptConnectionsTcs.Task.ConfigureAwait(false);
                }
                catch (OperationCanceledException ex) when (timeoutCts.IsCancellationRequested)
                {
                    var timeoutMessage = string.Format(
                        CultureInfo.InvariantCulture,
                        "MongoDB connection availability took longer than the specified timeout of {0} seconds. Consider increasing the value of '{1}'.",
                        this.Options.ConnectionTimeout.TotalSeconds,
                        nameof(this.Options.ConnectionTimeout));

                    throw new TimeoutException(timeoutMessage, ex);
                }
            }
        }
        finally
        {
            this.Process.OutputDataReceived -= OnOutputDataReceivedForConnectionReadiness;
            this.Process.Exited -= OnProcessExited;
        }
    }

    private async Task ConfigureAndWaitForReplicaSetReadinessAsync(CancellationToken cancellationToken)
    {
        var isReplicaSetReadyTcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var isTransactionReadyTcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);

        void OnProcessExited(object sender, EventArgs e)
        {
            var exception = this.Process.ExitCode == 0
                ? new InvalidOperationException($"The process MongoDB process {this.Process.Id} exited unexpectedly.")
                : new InvalidOperationException($"The process MongoDB process {this.Process.Id} exited unexpectedly with code {this.Process.ExitCode}.");

            isReplicaSetReadyTcs.TrySetException(exception);
            isTransactionReadyTcs.TrySetException(exception);
        }

        void OnClusterDescriptionChanged(ClusterDescriptionChangedEvent evt)
        {
            if (evt.NewDescription.Servers.Any(x => x.Type == ServerType.ReplicaSetPrimary && x.State == ServerState.Connected))
            {
                isReplicaSetReadyTcs.TrySetResult(true);
            }

            if (evt.NewDescription.Servers.Any(x => x.State == ServerState.Connected && x.IsDataBearing))
            {
                isTransactionReadyTcs.TrySetResult(true);
            }
        }

        this.Process.Exited += OnProcessExited;

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

            using (var timeoutCts = new CancellationTokenSource(this.Options.ReplicaSetSetupTimeout))
            using (var combinedCts = CancellationTokenSource.CreateLinkedTokenSource(timeoutCts.Token, cancellationToken))
            using (combinedCts.Token.Register(() =>
            {
                isReplicaSetReadyTcs.TrySetCanceled(combinedCts.Token);
                isTransactionReadyTcs.TrySetCanceled(combinedCts.Token);
            }))
            {
                var command = new BsonDocument("replSetInitiate", replConfig);

                try
                {
                    await admin.RunCommandAsync<BsonDocument>(command, cancellationToken: combinedCts.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (timeoutCts.IsCancellationRequested)
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
                    await isReplicaSetReadyTcs.Task.ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (timeoutCts.IsCancellationRequested)
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
                    await isTransactionReadyTcs.Task.ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (timeoutCts.IsCancellationRequested)
                {
                    var timeoutMessage = string.Format(
                        CultureInfo.InvariantCulture,
                        "Cluster readiness for transactions took longer than the specified timeout of {0} seconds. Consider increasing the value of '{1}'.",
                        this.Options.ReplicaSetSetupTimeout.TotalSeconds,
                        nameof(this.Options.ReplicaSetSetupTimeout));

                    throw new TimeoutException(timeoutMessage);
                }
            }
        }
        finally
        {
            this.Process.Exited -= OnProcessExited;
        }
    }
}