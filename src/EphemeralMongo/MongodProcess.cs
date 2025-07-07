using System.ComponentModel;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Text;
using System.Text.Json;
using MongoDB.Bson;
using MongoDB.Driver;
using MongoDB.Driver.Core.Events;
using MongoDB.Driver.Core.Servers;

namespace EphemeralMongo;

internal sealed class MongodProcess : BaseMongoProcess
{
    private const string ConnectionReadySentence = "waiting for connections";

    // https://github.com/search?q=%22STATUS_DLL_NOT_FOUND%22+%223221225781%22&type=code
    [SuppressMessage("ReSharper", "InconsistentNaming", Justification = "STATUS_DLL_NOT_FOUND is a constant from the Windows API")]
    private const uint STATUS_DLL_NOT_FOUND = 3221225781; // "-1073741515" as a signed int returned by Process.ExitCode

    private readonly StringBuilder _startupErrors = new StringBuilder();

    public MongodProcess(MongoRunnerOptions options, string executablePath, string[] arguments)
        : base(options, executablePath, arguments)
    {
    }

    public override async Task StartAsync(CancellationToken cancellationToken)
    {
        this.Process.OutputDataReceived += this.OnOutputDataReceivedCaptureStartupErrors;
        this.Process.ErrorDataReceived += this.OnErrorDataReceivedCaptureStartupErrors;

        try
        {
            await this.StartAndWaitForConnectionReadinessAsync(cancellationToken).ConfigureAwait(false);

            if (this.Options.UseSingleNodeReplicaSet)
            {
                await this.ConfigureAndWaitForReplicaSetReadinessAsync(cancellationToken).ConfigureAwait(false);
            }
        }
        finally
        {
            this.Process.OutputDataReceived -= this.OnOutputDataReceivedCaptureStartupErrors;
            this.Process.ErrorDataReceived -= this.OnErrorDataReceivedCaptureStartupErrors;

            this._startupErrors.Clear();
        }
    }

    private async Task StartAndWaitForConnectionReadinessAsync(CancellationToken cancellationToken)
    {
        var isReadyToAcceptConnectionsTcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);

        void OnOutputDataReceivedForConnectionReadiness(object sender, DataReceivedEventArgs args)
        {
#if NETSTANDARD2_0 || NETFRAMEWORK
            var isReadyToAcceptConnections = args.Data != null && args.Data.IndexOf(ConnectionReadySentence, StringComparison.OrdinalIgnoreCase) >= 0;
#else
            var isReadyToAcceptConnections = args.Data != null && args.Data.Contains(ConnectionReadySentence, StringComparison.OrdinalIgnoreCase);
#endif
            if (isReadyToAcceptConnections)
            {
                isReadyToAcceptConnectionsTcs.TrySetResult(true);
            }
        }

        void OnProcessExited(object? sender, EventArgs e)
        {
            isReadyToAcceptConnectionsTcs.TrySetResult(false);
        }

        this.Process.OutputDataReceived += OnOutputDataReceivedForConnectionReadiness;
        this.Process.Exited += OnProcessExited;

        try
        {
            await this.Process.StartProcessWithRetryAsync(cancellationToken).ConfigureAwait(false);

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

                this.HandleUnexpectedProcessExit();
            }
        }
        finally
        {
            this.Process.OutputDataReceived -= OnOutputDataReceivedForConnectionReadiness;
            this.Process.Exited -= OnProcessExited;
        }
    }

    private void HandleUnexpectedProcessExit()
    {
        if (this.Process.HasExited)
        {
            // WaitForExit ensure that all output is flushed before we throw the exception,
            // ensuring we asynchronously capture all standard output and error messages.
            this.Process.WaitForExit();
            throw this.CreateExceptionFromUnexpectedProcessExit();
        }
    }

    private EphemeralMongoException CreateExceptionFromUnexpectedProcessExit()
    {
        var exitCode = (uint)this.Process.ExitCode;
        var processDescription = $"{this.Process.StartInfo.FileName} {this.Process.StartInfo.Arguments}";
        var startupErrors = this._startupErrors.ToString();
        var startupErrorsMessage = string.IsNullOrWhiteSpace(startupErrors) ? string.Empty : $" Output: {startupErrors}";

        return exitCode switch
        {
            0 => new EphemeralMongoException($"The MongoDB process '{processDescription}' exited unexpectedly.{startupErrorsMessage}"),

            // Happens on Windows for MongoDB Enterprise edition when sasl2.dll is missing
            STATUS_DLL_NOT_FOUND => new EphemeralMongoException($"The MongoDB process '{processDescription}' exited unexpectedly with code {exitCode}. This is likely due to a missing DLL.{startupErrorsMessage}"),

            _ => new EphemeralMongoException($"The MongoDB process '{processDescription}' exited unexpectedly with code {exitCode}.{startupErrorsMessage}")
        };
    }

    private async Task ConfigureAndWaitForReplicaSetReadinessAsync(CancellationToken cancellationToken)
    {
        var isReplicaSetReadyTcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var isTransactionReadyTcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);

        void OnProcessExited(object? sender, EventArgs e)
        {
            isReplicaSetReadyTcs.TrySetResult(false);
            isTransactionReadyTcs.TrySetResult(false);
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

                this.HandleUnexpectedProcessExit();
            }
        }
        finally
        {
            this.Process.Exited -= OnProcessExited;
        }
    }

    private void OnOutputDataReceivedCaptureStartupErrors(object sender, DataReceivedEventArgs args)
    {
        if (args.Data == null)
        {
            return;
        }

        try
        {
            // Here's the kind of document we're trying to parse:
            // {
            //   "t": {
            //     "$date": "2025-04-18T22:15:02.064-04:00"
            //   },
            //   "s": "E",
            //   "c": "CONTROL",
            //   "id": 20568,
            //   "ctx": "initandlisten",
            //   "msg": "Error setting up listener",
            //   "attr": {
            //     "error": {
            //       "code": 9001,
            //       "codeName": "SocketException",
            //       "errmsg": "127.0.0.1:58905 :: caused by :: setup bind :: caused by :: An attempt was made to access a socket in a way forbidden by its access permissions."
            //     }
            //   }
            // }
            using var document = JsonDocument.Parse(args.Data);

            if (document.RootElement.TryGetProperty("s", out var sevEl) && sevEl.ValueKind == JsonValueKind.String && sevEl.GetString() is "E" or "F")
            {
                this._startupErrors.AppendLine(args.Data);
            }
        }
        catch
        {
            // Startup error messages (like invalid arguments) are also sent to standard output in plain text, not as structured JSON
            this._startupErrors.AppendLine(args.Data);
        }
    }

    private void OnErrorDataReceivedCaptureStartupErrors(object sender, DataReceivedEventArgs args)
    {
        if (args.Data != null)
        {
            this._startupErrors.AppendLine(args.Data);
        }
    }
}

file static class ProcessExtensions
{
    [SuppressMessage("Usage", "CA2249:Consider using \'string.Contains\' instead of \'string.IndexOf\'", Justification = "Not worth it due to multi-targeting")]
    public static async Task StartProcessWithRetryAsync(this Process process, CancellationToken cancellationToken)
    {
        const int maxAttempts = 3;
        const int retryDelayMs = 50;

        for (var attempt = 1; attempt <= maxAttempts; attempt++)
        {
            try
            {
                process.Start();
                return;
            }
            // This exception rarely happens on Linux in CI during tests with high concurrency
            // System.ComponentModel.Win32Exception : An error occurred trying to start process '<omitted>/mongod' with working directory '<omitted>'. Text file busy
            catch (Win32Exception ex) when (ex.Message.IndexOf("text file busy", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                if (attempt == maxAttempts)
                {
                    throw;
                }

                await Task.Delay(retryDelayMs, cancellationToken).ConfigureAwait(false);
            }
        }
    }
}
