using System.Diagnostics.CodeAnalysis;

namespace EphemeralMongo;

[Experimental("EMEX0001")]
[SuppressMessage("Maintainability", "CA1513", Justification = "ObjectDisposedException.ThrowIf isn't worth it when multi-targeting")]
public sealed class MongoRunnerPool : IDisposable
{
    private readonly Guid _id;
    private readonly MongoRunnerOptions _options;
    private readonly int _maxRentalsPerRunner;
    private readonly SemaphoreSlim _mutex = new SemaphoreSlim(1, 1);
    private readonly HashSet<PooledMongoRunnerInfo> _runners = [];
    private int _isDisposed;

    public MongoRunnerPool(MongoRunnerOptions options, int maxRentalsPerRunner = 100)
    {
        this._id = Guid.NewGuid();
        this._options = options ?? throw new ArgumentNullException(nameof(options));
        this._maxRentalsPerRunner = maxRentalsPerRunner < 1
            ? throw new ArgumentOutOfRangeException(nameof(maxRentalsPerRunner), "Maximum rentals per runner must be greater than 0")
            : maxRentalsPerRunner;
    }

    public async Task<IMongoRunner> RentAsync(CancellationToken cancellationToken = default)
    {
        return await this.RentInternalAsync(cancellationToken).ConfigureAwait(false);
    }

    private async Task<PooledMongoRunner> RentInternalAsync(CancellationToken cancellationToken)
    {
        if (Interlocked.CompareExchange(ref this._isDisposed, 0, 0) == 1)
        {
            throw new ObjectDisposedException(nameof(MongoRunnerPool));
        }

        await this._mutex.WaitAsync(cancellationToken).ConfigureAwait(false);

        try
        {
            if (this._runners.FirstOrDefault(r => r.TotalRentals < this._maxRentalsPerRunner) is { } availableRunner)
            {
                availableRunner.TotalRentals++;
                return new PooledMongoRunner(availableRunner);
            }

            var underlyingRunner = await MongoRunner.RunAsync(this._options, cancellationToken).ConfigureAwait(false);
            var runnerInfo = new PooledMongoRunnerInfo(underlyingRunner, this._id);
            this._runners.Add(runnerInfo);

            return new PooledMongoRunner(runnerInfo);
        }
        finally
        {
            this._mutex.Release();
        }
    }

    public IMongoRunner Rent(CancellationToken cancellationToken = default)
    {
        return this.RentAsync(cancellationToken).ConfigureAwait(false).GetAwaiter().GetResult();
    }

    public void Return(IMongoRunner runner)
    {
        if (runner == null)
        {
            throw new ArgumentNullException(nameof(runner));
        }

        if (Interlocked.CompareExchange(ref this._isDisposed, 0, 0) == 1)
        {
            throw new ObjectDisposedException(nameof(MongoRunnerPool));
        }

        if (runner is not PooledMongoRunner pooledRunner)
        {
            throw new ArgumentException("The returned runner was not pooled", nameof(runner));
        }

        if (pooledRunner.Info.ParentPoolId != this._id)
        {
            throw new ArgumentException("The returned runner was not rented from this pool", nameof(runner));
        }

        this.ReturnInternal(pooledRunner);
    }

    private void ReturnInternal(PooledMongoRunner pooledRunner)
    {
        this._mutex.Wait();

        var disposeUnderlyingRunner = false;

        try
        {
            if (!pooledRunner.Info.RentedBy.Remove(pooledRunner.Id))
            {
                // already returned
                return;
            }

            if (pooledRunner.Info.RentedBy.Count == 0 || pooledRunner.Info.TotalRentals >= this._maxRentalsPerRunner)
            {
                this._runners.Remove(pooledRunner.Info);
                disposeUnderlyingRunner = true;
            }
        }
        finally
        {
            this._mutex.Release();
        }

        if (disposeUnderlyingRunner)
        {
            pooledRunner.Info.UnderlyingRunner.Dispose();
        }
    }

    public void Dispose()
    {
        if (Interlocked.CompareExchange(ref this._isDisposed, 1, 0) == 1)
        {
            return;
        }

        List<PooledMongoRunnerInfo>? runnersToDispose;

        this._mutex.Wait();

        try
        {
            runnersToDispose = [.. this._runners];
            this._runners.Clear();
        }
        finally
        {
            this._mutex.Release();
        }

        if (runnersToDispose.Count > 0)
        {
            foreach (var runner in runnersToDispose)
            {
                try
                {
                    runner.UnderlyingRunner.Dispose();
                }
                catch
                {
                    // ignore
                }
            }

            try
            {
                this._mutex.Dispose();
            }
            catch
            {
                // ignore
            }
        }
        else
        {
            this._mutex.Dispose();
        }
    }

    private sealed class PooledMongoRunnerInfo(IMongoRunner underlyingRunner, Guid parentPoolId)
    {
        public IMongoRunner UnderlyingRunner { get; } = underlyingRunner;

        public Guid ParentPoolId { get; } = parentPoolId;

        public int TotalRentals { get; set; } = 1;

        public HashSet<Guid> RentedBy { get; } = [];
    }

    private sealed class PooledMongoRunner : IMongoRunner
    {
        public PooledMongoRunner(PooledMongoRunnerInfo info)
        {
            this.Id = Guid.NewGuid();
            this.Info = info;

            info.RentedBy.Add(this.Id);
        }

        public Guid Id { get; }

        public PooledMongoRunnerInfo Info { get; }

        public string ConnectionString => this.Info.UnderlyingRunner.ConnectionString;

        public Task ImportAsync(string database, string collection, string inputFilePath, string[]? additionalArguments = null, bool drop = false, CancellationToken cancellationToken = default)
        {
            return this.Info.UnderlyingRunner.ImportAsync(database, collection, inputFilePath, additionalArguments, drop, cancellationToken);
        }

        public void Import(string database, string collection, string inputFilePath, string[]? additionalArguments = null, bool drop = false, CancellationToken cancellationToken = default)
        {
            this.Info.UnderlyingRunner.Import(database, collection, inputFilePath, additionalArguments, drop, cancellationToken);
        }

        public Task ExportAsync(string database, string collection, string outputFilePath, string[]? additionalArguments = null, CancellationToken cancellationToken = default)
        {
            return this.Info.UnderlyingRunner.ExportAsync(database, collection, outputFilePath, additionalArguments, cancellationToken);
        }

        public void Export(string database, string collection, string outputFilePath, string[]? additionalArguments = null, CancellationToken cancellationToken = default)
        {
            this.Info.UnderlyingRunner.Export(database, collection, outputFilePath, additionalArguments, cancellationToken);
        }

        public void Dispose()
        {
            // Prevent end-users from disposing the underlying runner, as this could impact other rentals.
            // Let the pool manage the disposal of runners.
        }
    }
}