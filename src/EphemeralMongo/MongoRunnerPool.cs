using System.Diagnostics.CodeAnalysis;

namespace EphemeralMongo;

[Experimental("EMEX0001")]
[SuppressMessage("Maintainability", "CA1513", Justification = "ObjectDisposedException.ThrowIf isn't worth it when multi-targeting")]
[SuppressMessage("Maintainability", "CA1510", Justification = "ArgumentNullException.ThrowIfNull isn't worth it when multi-targeting")]
public sealed class MongoRunnerPool : IDisposable
{
    private static readonly TimeSpan DefaultIdleTimeout = TimeSpan.FromSeconds(10);

    private readonly Func<CancellationToken, Task<IMongoRunner>> _runnerFactory;
    private readonly int _maxRentalsPerRunner;
    private readonly TimeSpan _idleTimeout;
    private readonly Func<TimeSpan, CancellationToken, Task> _delay;
    private readonly Logger? _errorLogger;

    // Guards every mutable field below, as well as the mutable state of each RunnerSlot
    private readonly object _lock = new();
    private readonly CancellationTokenSource _disposeCts = new();

    // Every runner that is starting or alive, including retired ones that still have outstanding rentals
    private readonly HashSet<RunnerSlot> _slots = [];

    // The runner new rentals are served from, null when none exists yet or the previous one was retired
    private RunnerSlot? _current;
    private bool _isDisposed;

    public MongoRunnerPool(MongoRunnerOptions options, int maxRentalsPerRunner = 100)
        : this(CreateRunnerFactory(options), maxRentalsPerRunner, DefaultIdleTimeout, Task.Delay, options?.StandardErrorLogger)
    {
    }

    internal MongoRunnerPool(Func<CancellationToken, Task<IMongoRunner>> runnerFactory, int maxRentalsPerRunner, TimeSpan idleTimeout, Func<TimeSpan, CancellationToken, Task> delay, Logger? errorLogger)
    {
        this._runnerFactory = runnerFactory;
        this._maxRentalsPerRunner = maxRentalsPerRunner < 1
            ? throw new ArgumentOutOfRangeException(nameof(maxRentalsPerRunner), "Maximum rentals per runner must be greater than 0")
            : maxRentalsPerRunner;
        this._idleTimeout = idleTimeout;
        this._delay = delay;
        this._errorLogger = errorLogger;
    }

    private static Func<CancellationToken, Task<IMongoRunner>> CreateRunnerFactory(MongoRunnerOptions options)
    {
        if (options == null)
        {
            throw new ArgumentNullException(nameof(options));
        }

        // Retired and current runners can be alive at the same time, so they can't share a port or a data directory
        if (options.MongoPort != null)
        {
            throw new ArgumentException($"{nameof(MongoRunnerOptions.MongoPort)} can't be set when using a pool, as multiple runners may be alive at the same time", nameof(options));
        }

        if (options.DataDirectory != null)
        {
            throw new ArgumentException($"{nameof(MongoRunnerOptions.DataDirectory)} can't be set when using a pool, as multiple runners may be alive at the same time", nameof(options));
        }

        // Snapshot the options so that later changes made by the caller don't affect runners started by this pool
        var optionsCopy = new MongoRunnerOptions(options);
        return cancellationToken => MongoRunner.RunAsync(optionsCopy, cancellationToken);
    }

    public async Task<IMongoRunner> RentAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        RunnerSlot slot;
        var mustStartSlot = false;

        lock (this._lock)
        {
            if (this._isDisposed)
            {
                throw new ObjectDisposedException(nameof(MongoRunnerPool));
            }

            if (this._current == null)
            {
                this._current = new RunnerSlot(this._disposeCts.Token);
                this._slots.Add(this._current);
                mustStartSlot = true;
            }

            slot = this._current;
            slot.TotalRentals++;
            slot.ActiveRentals++;

            // Invalidates any pending idle expiration
            slot.IdleVersion++;

            if (slot.TotalRentals >= this._maxRentalsPerRunner)
            {
                // Retire the runner, it will be stopped once all its rentals are returned
                this._current = null;
            }
        }

        if (mustStartSlot)
        {
            // Run the startup outside of the caller's context, so it doesn't depend on the caller's cancellation token
            // as other callers may be waiting for this same runner
            _ = Task.Run(() => this.StartSlotAsync(slot), CancellationToken.None);
        }

        IMongoRunner underlyingRunner;

        try
        {
            underlyingRunner = await WaitAsync(slot.Startup.Task, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested && Volatile.Read(ref this._isDisposed))
        {
            this.Release(slot);
#pragma warning disable MA0054 // The cancellation is an implementation detail of the disposal, the caller only needs to know the pool is disposed
            throw new ObjectDisposedException(nameof(MongoRunnerPool));
#pragma warning restore MA0054
        }
        catch
        {
            this.Release(slot);
            throw;
        }

        if (Volatile.Read(ref this._isDisposed))
        {
            // The pool was disposed while this rental was waiting for the runner to start
            throw new ObjectDisposedException(nameof(MongoRunnerPool));
        }

        return new RentedMongoRunner(this, slot, underlyingRunner);
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

        if (runner is not RentedMongoRunner rentedRunner || rentedRunner.Pool != this)
        {
            throw new ArgumentException("The runner was not rented from this pool", nameof(runner));
        }

        rentedRunner.Dispose();
    }

    private async Task StartSlotAsync(RunnerSlot slot)
    {
        try
        {
            var runner = await this._runnerFactory(slot.StartupCancellationToken).ConfigureAwait(false);
            slot.Startup.TrySetResult(runner);
        }
        catch (Exception ex)
        {
            lock (this._lock)
            {
                // Ensure that callers waiting for this runner, or renting after this point, never get this failed runner again.
                // This must happen before the startup task completes so that returning rentals don't put it in idle mode.
                if (this._current == slot)
                {
                    this._current = null;
                }

                if (slot.ActiveRentals == 0)
                {
                    this._slots.Remove(slot);
                }
            }

            if (ex is OperationCanceledException && slot.StartupCancellationToken.IsCancellationRequested)
            {
                slot.Startup.TrySetCanceled(slot.StartupCancellationToken);
            }
            else
            {
                slot.Startup.TrySetException(ex);
            }
        }
    }

    private void Release(RunnerSlot slot)
    {
        var mustStopSlot = false;
        var mustExpireWhenIdle = false;
        var idleVersion = 0;
        var idleCancellationToken = CancellationToken.None;

        lock (this._lock)
        {
            if (this._isDisposed)
            {
                // All runners were already stopped by the pool disposal
                return;
            }

            slot.ActiveRentals--;

            if (slot.ActiveRentals > 0)
            {
                return;
            }

            if (this._current == slot)
            {
                // Keep the runner alive for a little while in case it's rented again soon
                mustExpireWhenIdle = true;
                idleVersion = ++slot.IdleVersion;
                idleCancellationToken = this._disposeCts.Token;
            }
            else
            {
                mustStopSlot = this._slots.Remove(slot);
            }
        }

        if (mustStopSlot)
        {
            this.StopSlotQuietly(slot);
        }
        else if (mustExpireWhenIdle)
        {
            _ = this.ExpireWhenIdleAsync(slot, idleVersion, idleCancellationToken);
        }
    }

    private async Task ExpireWhenIdleAsync(RunnerSlot slot, int idleVersion, CancellationToken cancellationToken)
    {
        try
        {
            await this._delay(this._idleTimeout, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // The pool was disposed and took care of stopping the runner
            return;
        }

        lock (this._lock)
        {
            if (this._isDisposed || slot.IdleVersion != idleVersion)
            {
                // Rented again since it became idle, or already handled by the pool disposal
                return;
            }

            if (this._current == slot)
            {
                this._current = null;
            }

            if (!this._slots.Remove(slot))
            {
                return;
            }
        }

        this.StopSlotQuietly(slot);
    }

    private void StopSlotQuietly(RunnerSlot slot)
    {
        // A slot can be stopped while its runner is still starting when all the callers waiting for it gave up
        _ = slot.Startup.Task.ContinueWith(
            static (startupTask, state) =>
            {
                var (pool, slot) = ((MongoRunnerPool, RunnerSlot))state!;

                if (startupTask.Status == TaskStatus.RanToCompletion)
                {
                    pool.StopRunnerQuietly(slot, startupTask.Result);
                }
            },
            (this, slot),
            CancellationToken.None,
            TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);
    }

    public void Dispose()
    {
        List<RunnerSlot> slots;

        lock (this._lock)
        {
            if (this._isDisposed)
            {
                return;
            }

            Volatile.Write(ref this._isDisposed, true);
            slots = [.. this._slots];
            this._slots.Clear();
            this._current = null;
        }

        // Abort runners that are still starting and pending idle expirations
        this._disposeCts.Cancel();

        foreach (var slot in slots)
        {
            IMongoRunner runner;

            try
            {
                runner = slot.Startup.Task.ConfigureAwait(false).GetAwaiter().GetResult();
            }
            catch
            {
                // The runner failed to start or its startup was canceled, there's nothing to stop
                continue;
            }

            this.StopRunnerQuietly(slot, runner);
        }

        this._disposeCts.Dispose();
    }

    private void StopRunnerQuietly(RunnerSlot slot, IMongoRunner runner)
    {
        try
        {
            slot.StopRunner(runner);
        }
        catch (Exception ex)
        {
            // Stopping a runner is a side effect of returning it or disposing the pool, it shouldn't fail the caller
            this._errorLogger?.Invoke($"An error occurred while stopping a pooled MongoDB runner: {ex}");
        }
    }

    private static async Task<T> WaitAsync<T>(Task<T> task, CancellationToken cancellationToken)
    {
#if NET8_0_OR_GREATER
        return await task.WaitAsync(cancellationToken).ConfigureAwait(false);
#else
        if (!cancellationToken.CanBeCanceled || task.IsCompleted)
        {
            return await task.ConfigureAwait(false);
        }

        var cancellationTcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);

        using (cancellationToken.Register(static state => ((TaskCompletionSource<bool>)state!).TrySetResult(true), cancellationTcs))
        {
            if (await Task.WhenAny(task, cancellationTcs.Task).ConfigureAwait(false) != task)
            {
                throw new OperationCanceledException(cancellationToken);
            }
        }

        return await task.ConfigureAwait(false);
#endif
    }

    private sealed class RunnerSlot(CancellationToken startupCancellationToken)
    {
        private int _isStopped;

        public TaskCompletionSource<IMongoRunner> Startup { get; } = new TaskCompletionSource<IMongoRunner>(TaskCreationOptions.RunContinuationsAsynchronously);

        public CancellationToken StartupCancellationToken { get; } = startupCancellationToken;

        // Number of times this runner was handed out, used to decide when to retire it
        public int TotalRentals { get; set; }

        // Number of rentals that haven't been returned yet
        public int ActiveRentals { get; set; }

        // Incremented each time the runner is rented or becomes idle, so that stale idle expirations are ignored
        public int IdleVersion { get; set; }

        public void StopRunner(IMongoRunner runner)
        {
            // Both the pool disposal and a return or idle expiration may race to stop the same runner
            if (Interlocked.Exchange(ref this._isStopped, 1) == 0)
            {
                runner.Dispose();
            }
        }
    }

    private sealed class RentedMongoRunner(MongoRunnerPool pool, RunnerSlot slot, IMongoRunner underlyingRunner) : IMongoRunner
    {
        private int _isReturned;

        public MongoRunnerPool Pool { get; } = pool;

        public string ConnectionString
        {
            get
            {
                this.ThrowIfUnusable();
                return underlyingRunner.ConnectionString;
            }
        }

        public Task ImportAsync(string database, string collection, string inputFilePath, string[]? additionalArguments = null, bool drop = false, CancellationToken cancellationToken = default)
        {
            this.ThrowIfUnusable();
            return underlyingRunner.ImportAsync(database, collection, inputFilePath, additionalArguments, drop, cancellationToken);
        }

        public void Import(string database, string collection, string inputFilePath, string[]? additionalArguments = null, bool drop = false, CancellationToken cancellationToken = default)
        {
            this.ThrowIfUnusable();
            underlyingRunner.Import(database, collection, inputFilePath, additionalArguments, drop, cancellationToken);
        }

        public Task ExportAsync(string database, string collection, string outputFilePath, string[]? additionalArguments = null, CancellationToken cancellationToken = default)
        {
            this.ThrowIfUnusable();
            return underlyingRunner.ExportAsync(database, collection, outputFilePath, additionalArguments, cancellationToken);
        }

        public void Export(string database, string collection, string outputFilePath, string[]? additionalArguments = null, CancellationToken cancellationToken = default)
        {
            this.ThrowIfUnusable();
            underlyingRunner.Export(database, collection, outputFilePath, additionalArguments, cancellationToken);
        }

        private void ThrowIfUnusable()
        {
            if (Volatile.Read(ref this._isReturned) == 1)
            {
                throw new ObjectDisposedException("This MongoDB runner was returned to the pool");
            }

            if (Volatile.Read(ref this.Pool._isDisposed))
            {
                throw new ObjectDisposedException(nameof(MongoRunnerPool));
            }
        }

        // Disposing a rented runner returns it to the pool
        public void Dispose()
        {
            if (Interlocked.Exchange(ref this._isReturned, 1) == 0)
            {
                this.Pool.Release(slot);
            }
        }
    }
}
