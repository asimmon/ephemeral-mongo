using System.Diagnostics.CodeAnalysis;

namespace EphemeralMongo;

[Experimental("EMEX0001")]
[SuppressMessage("Maintainability", "CA1513", Justification = "ObjectDisposedException.ThrowIf isn't worth it when multi-targeting")]
public sealed class MongoRunnerPool : IDisposable
{
    private readonly MongoRunnerOptions _options;
    private readonly int _maxRentalsPerRunner;
    private readonly SemaphoreSlim _mutex = new SemaphoreSlim(1, 1);
    private readonly HashSet<PooledMongoRunner> _pooledRunners = [];
    private bool _isDisposed;

    public MongoRunnerPool(MongoRunnerOptions options, int maxRentalsPerRunner = 100)
    {
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
        await this._mutex.WaitAsync(cancellationToken).ConfigureAwait(false);

        try
        {
            if (this._isDisposed)
            {
                throw new ObjectDisposedException(nameof(MongoRunnerPool));
            }

            if (this._pooledRunners.FirstOrDefault(r => r.TotalRentals < this._maxRentalsPerRunner) is { } availablePooledRunner)
            {
                availablePooledRunner.ReferenceCount++;
                availablePooledRunner.TotalRentals++;

                return availablePooledRunner;
            }

            var underlyingRunner = await MongoRunner.RunAsync(this._options, cancellationToken).ConfigureAwait(false);
            var pooledRunner = new PooledMongoRunner(this, underlyingRunner);
            this._pooledRunners.Add(pooledRunner);

            return pooledRunner;
        }
        finally
        {
            this._mutex.Release();
        }
    }

    public IMongoRunner Rent(CancellationToken cancellationToken = default)
    {
        return this.RentInternal(cancellationToken);
    }

    private PooledMongoRunner RentInternal(CancellationToken cancellationToken)
    {
        this._mutex.Wait(cancellationToken);

        try
        {
            if (this._isDisposed)
            {
                throw new ObjectDisposedException(nameof(MongoRunnerPool));
            }

            if (this._pooledRunners.FirstOrDefault(r => r.TotalRentals < this._maxRentalsPerRunner) is { } availablePooledRunner)
            {
                availablePooledRunner.ReferenceCount++;
                availablePooledRunner.TotalRentals++;

                return availablePooledRunner;
            }

            var underlyingRunner = MongoRunner.Run(this._options, cancellationToken);
            var pooledRunner = new PooledMongoRunner(this, underlyingRunner);
            this._pooledRunners.Add(pooledRunner);

            return pooledRunner;
        }
        finally
        {
            this._mutex.Release();
        }
    }

    public void Return(IMongoRunner runner)
    {
        if (runner == null)
        {
            throw new ArgumentNullException(nameof(runner));
        }

        if (runner is not PooledMongoRunner pooledRunner || pooledRunner.Pool != this)
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
            if (this._isDisposed)
            {
                throw new ObjectDisposedException(nameof(MongoRunnerPool));
            }

            if (!this._pooledRunners.Contains(pooledRunner))
            {
                return;
            }

            pooledRunner.ReferenceCount--;

            if (pooledRunner.ReferenceCount <= 0 || pooledRunner.TotalRentals >= this._maxRentalsPerRunner)
            {
                this._pooledRunners.Remove(pooledRunner);
                disposeUnderlyingRunner = true;
            }
        }
        finally
        {
            this._mutex.Release();
        }

        if (disposeUnderlyingRunner)
        {
            pooledRunner.UnderlyingRunner.Dispose();
        }
    }

    public void Dispose()
    {
        if (this._isDisposed)
        {
            return;
        }

        List<PooledMongoRunner>? runnersToDispose;

        this._mutex.Wait();

        try
        {
            if (this._isDisposed)
            {
                return;
            }

            this._isDisposed = true;

            runnersToDispose = [.. this._pooledRunners];
            this._pooledRunners.Clear();
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

    private sealed class PooledMongoRunner(MongoRunnerPool pool, IMongoRunner underlyingRunner) : IMongoRunner
    {
        public MongoRunnerPool Pool { get; } = pool;

        public IMongoRunner UnderlyingRunner { get; } = underlyingRunner;

        public int ReferenceCount { get; set; } = 1;

        public int TotalRentals { get; set; } = 1;

        public string ConnectionString => this.UnderlyingRunner.ConnectionString;

        public Task ImportAsync(string database, string collection, string inputFilePath, string[]? additionalArguments = null, bool drop = false, CancellationToken cancellationToken = default)
        {
            return this.UnderlyingRunner.ImportAsync(database, collection, inputFilePath, additionalArguments, drop, cancellationToken);
        }

        public void Import(string database, string collection, string inputFilePath, string[]? additionalArguments = null, bool drop = false, CancellationToken cancellationToken = default)
        {
            this.UnderlyingRunner.Import(database, collection, inputFilePath, additionalArguments, drop, cancellationToken);
        }

        public Task ExportAsync(string database, string collection, string outputFilePath, string[]? additionalArguments = null, CancellationToken cancellationToken = default)
        {
            return this.UnderlyingRunner.ExportAsync(database, collection, outputFilePath, additionalArguments, cancellationToken);
        }

        public void Export(string database, string collection, string outputFilePath, string[]? additionalArguments = null, CancellationToken cancellationToken = default)
        {
            this.UnderlyingRunner.Export(database, collection, outputFilePath, additionalArguments, cancellationToken);
        }

        public void Dispose()
        {
            // Prevent end-users from disposing the underlying runner, as this could impact other rentals.
            // Let the pool manage the disposal of runners.
        }
    }
}