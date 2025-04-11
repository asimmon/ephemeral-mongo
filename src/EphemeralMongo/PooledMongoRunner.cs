using System.Diagnostics.CodeAnalysis;

namespace EphemeralMongo;

[Experimental("EMEX0001")]
[SuppressMessage("Maintainability", "CA1513", Justification = "ObjectDisposedException.ThrowIf isn't worth it when multi-targeting")]
public sealed class PooledMongoRunner : IDisposable
{
    private readonly MongoRunnerOptions _options;
    private readonly int _maxRentalsPerRunner;
    private readonly SemaphoreSlim _mutex = new SemaphoreSlim(1, 1);
    private readonly List<RunnerInfo> _runners = [];
    private bool _isDisposed;

    public PooledMongoRunner(MongoRunnerOptions options, int maxRentalsPerRunner = 100)
    {
        this._options = options ?? throw new ArgumentNullException(nameof(options));
        this._maxRentalsPerRunner = maxRentalsPerRunner < 1
            ? throw new ArgumentOutOfRangeException(nameof(maxRentalsPerRunner), "Maximum rentals per runner must be greater than 0")
            : maxRentalsPerRunner;
    }

    public async Task<IMongoRunner> RentAsync(CancellationToken cancellationToken = default)
    {
        await this._mutex.WaitAsync(cancellationToken).ConfigureAwait(false);

        try
        {
            if (this._isDisposed)
            {
                throw new ObjectDisposedException(nameof(PooledMongoRunner));
            }

            if (this._runners.FirstOrDefault(r => r.TotalRentals < this._maxRentalsPerRunner) is { } availableRunner)
            {
                availableRunner.ReferenceCount++;
                availableRunner.TotalRentals++;

                return availableRunner.Runner;
            }

            var newRunner = await MongoRunner.RunAsync(this._options, cancellationToken).ConfigureAwait(false);
            var newRunnerInfo = new RunnerInfo(newRunner);
            this._runners.Add(newRunnerInfo);

            return newRunner;
        }
        finally
        {
            this._mutex.Release();
        }
    }

    public IMongoRunner Rent(CancellationToken cancellationToken = default)
    {
        this._mutex.Wait(cancellationToken);

        try
        {
            if (this._isDisposed)
            {
                throw new ObjectDisposedException(nameof(PooledMongoRunner));
            }

            if (this._runners.FirstOrDefault(r => r.TotalRentals < this._maxRentalsPerRunner) is { } availableRunner)
            {
                availableRunner.ReferenceCount++;
                availableRunner.TotalRentals++;

                return availableRunner.Runner;
            }

            var newRunner = MongoRunner.Run(this._options, cancellationToken);
            var newRunnerInfo = new RunnerInfo(newRunner);
            this._runners.Add(newRunnerInfo);

            return newRunner;
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

        RunnerInfo? runnerToDispose = null;

        this._mutex.Wait();

        try
        {
            if (this._isDisposed)
            {
                throw new ObjectDisposedException(nameof(PooledMongoRunner));
            }

            var runnerInfo = this._runners.FirstOrDefault(r => runner == r.Runner);
            if (runnerInfo == null)
            {
                throw new InvalidOperationException("The returned runner was not rented from this pool");
            }

            runnerInfo.ReferenceCount--;

            if (runnerInfo.ReferenceCount <= 0 && runnerInfo.TotalRentals >= this._maxRentalsPerRunner)
            {
                runnerToDispose = runnerInfo;
                this._runners.Remove(runnerInfo);
            }
        }
        finally
        {
            this._mutex.Release();
        }

        runnerToDispose?.Runner.Dispose();
    }

    public void Dispose()
    {
        if (this._isDisposed)
        {
            return;
        }

        List<RunnerInfo>? runnersToDispose;

        this._mutex.Wait();

        try
        {
            if (this._isDisposed)
            {
                return;
            }

            this._isDisposed = true;

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
                    runner.Runner.Dispose();
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

    private class RunnerInfo(IMongoRunner runner)
    {
        public IMongoRunner Runner { get; } = runner;
        public int ReferenceCount { get; set; } = 1;
        public int TotalRentals { get; set; } = 1;
    }
}