#pragma warning disable EMEX0001 // Type is for evaluation purposes only
#pragma warning disable CA1849 // Synchronous APIs are tested on purpose, and CancelAsync isn't available on .NET Framework

namespace EphemeralMongo.Tests;

public class MongoRunnerPoolTests(ITestContextAccessor testContextAccessor)
{
    private CancellationToken CancellationToken => testContextAccessor.Current.CancellationToken;

    [Fact]
    public void Constructor_Throws_When_Options_Is_Null()
    {
        Assert.Throws<ArgumentNullException>(() => new MongoRunnerPool(null!));
    }

    [Fact]
    public void Constructor_Throws_When_MongoPort_Is_Set()
    {
        var ex = Assert.Throws<ArgumentException>(() => new MongoRunnerPool(new MongoRunnerOptions { MongoPort = 27017 }));

        Assert.Equal("options", ex.ParamName);
        Assert.Contains(nameof(MongoRunnerOptions.MongoPort), ex.Message);
    }

    [Fact]
    public void Constructor_Throws_When_DataDirectory_Is_Set()
    {
        var ex = Assert.Throws<ArgumentException>(() => new MongoRunnerPool(new MongoRunnerOptions { DataDirectory = Path.GetTempPath() }));

        Assert.Equal("options", ex.ParamName);
        Assert.Contains(nameof(MongoRunnerOptions.DataDirectory), ex.Message);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void Constructor_Throws_When_MaxRentalsPerRunner_Is_Not_Positive(int maxRentalsPerRunner)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new MongoRunnerPool(new MongoRunnerOptions(), maxRentalsPerRunner));
    }

    [Fact]
    public async Task RentAsync_Shares_Same_Runner_Until_Max_Rentals_Is_Reached()
    {
        var factory = new FakeRunnerFactory();
        using var pool = CreatePool(factory, maxRentalsPerRunner: 3);

        var rentals = new List<IMongoRunner>();
        for (var i = 0; i < 4; i++)
        {
            rentals.Add(await pool.RentAsync(this.CancellationToken));
        }

        Assert.Equal(2, factory.CreatedCount);
        Assert.Equal<string>(["fake://1", "fake://1", "fake://1", "fake://2"], rentals.Select(x => x.ConnectionString));
    }

    [Fact]
    public async Task RentAsync_Counts_Returned_Rentals_Toward_Max_Rentals()
    {
        var factory = new FakeRunnerFactory();
        using var pool = CreatePool(factory, maxRentalsPerRunner: 3);

        for (var i = 0; i < 3; i++)
        {
            pool.Return(await pool.RentAsync(this.CancellationToken));
        }

        var runner = await pool.RentAsync(this.CancellationToken);

        Assert.Equal("fake://2", runner.ConnectionString);
    }

    [Fact]
    public void Rent_Shares_Same_Runner_Until_Max_Rentals_Is_Reached()
    {
        var factory = new FakeRunnerFactory();
        using var pool = CreatePool(factory, maxRentalsPerRunner: 2);

        var runner1 = pool.Rent(this.CancellationToken);
        var runner2 = pool.Rent(this.CancellationToken);
        var runner3 = pool.Rent(this.CancellationToken);

        Assert.Equal("fake://1", runner1.ConnectionString);
        Assert.Equal("fake://1", runner2.ConnectionString);
        Assert.Equal("fake://2", runner3.ConnectionString);
    }

    [Fact]
    public async Task Retired_Runner_Stays_Alive_Until_All_Its_Rentals_Are_Returned()
    {
        var factory = new FakeRunnerFactory();
        using var pool = CreatePool(factory, maxRentalsPerRunner: 2);

        var runner1 = await pool.RentAsync(this.CancellationToken);
        var runner2 = await pool.RentAsync(this.CancellationToken);
        _ = await pool.RentAsync(this.CancellationToken);

        pool.Return(runner1);
        Assert.False(factory.Runners[0].IsDisposed);

        // No idle period for a runner that can't be rented anymore
        pool.Return(runner2);
        await AssertEventuallyAsync(() => factory.Runners[0].IsDisposed);
        Assert.False(factory.Runners[1].IsDisposed);
    }

    [Fact]
    public async Task Current_Runner_Stays_Alive_When_Idle_And_Is_Rented_Again()
    {
        var factory = new FakeRunnerFactory();
        var delay = new ManualDelay();
        using var pool = CreatePool(factory, delay: delay);

        pool.Return(await pool.RentAsync(this.CancellationToken));
        Assert.False(factory.Runners[0].IsDisposed);
        Assert.Equal(1, delay.PendingCount);

        var runner = await pool.RentAsync(this.CancellationToken);

        Assert.Equal(1, factory.CreatedCount);
        Assert.Equal("fake://1", runner.ConnectionString);

        // The idle period that started before the runner was rented again must have no effect
        delay.ElapseAll();
        Assert.False(factory.Runners[0].IsDisposed);
    }

    [Fact]
    public async Task Current_Runner_Is_Stopped_After_Idle_Timeout_And_Next_Rental_Starts_A_New_One()
    {
        var factory = new FakeRunnerFactory();
        var delay = new ManualDelay();
        using var pool = CreatePool(factory, delay: delay);

        pool.Return(await pool.RentAsync(this.CancellationToken));
        delay.ElapseAll();

        await AssertEventuallyAsync(() => factory.Runners[0].IsDisposed);

        var runner = await pool.RentAsync(this.CancellationToken);
        Assert.Equal("fake://2", runner.ConnectionString);
    }

    [Fact]
    public async Task Idle_Timeout_Uses_Configured_Duration()
    {
        var factory = new FakeRunnerFactory();
        var delay = new ManualDelay();
        using var pool = new MongoRunnerPool(factory.CreateAsync, maxRentalsPerRunner: 100, TimeSpan.FromSeconds(42), delay.DelayAsync, errorLogger: null);

        pool.Return(await pool.RentAsync(this.CancellationToken));

        Assert.Equal<TimeSpan>([TimeSpan.FromSeconds(42)], delay.RequestedDelays);
    }

    [Fact]
    public async Task Idle_Timeout_Only_Starts_When_All_Rentals_Are_Returned()
    {
        var factory = new FakeRunnerFactory();
        var delay = new ManualDelay();
        using var pool = CreatePool(factory, delay: delay);

        var runner1 = await pool.RentAsync(this.CancellationToken);
        var runner2 = await pool.RentAsync(this.CancellationToken);

        pool.Return(runner1);
        Assert.Equal(0, delay.PendingCount);

        pool.Return(runner2);
        Assert.Equal(1, delay.PendingCount);
    }

    [Fact]
    public async Task Concurrent_Rentals_Share_A_Single_Runner_Startup()
    {
        var startupGate = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var factory = new FakeRunnerFactory { BeforeCreate = (_, _) => startupGate.Task };
        using var pool = CreatePool(factory);

        var rentalTasks = Enumerable.Range(0, 5).Select(_ => pool.RentAsync(this.CancellationToken)).ToArray();
        startupGate.SetResult(true);
        var runners = await Task.WhenAll(rentalTasks);

        Assert.Equal(1, factory.CreatedCount);
        Assert.All(runners, x => Assert.Equal("fake://1", x.ConnectionString));
    }

    [Fact]
    public async Task Startup_Failure_Is_Propagated_To_All_Waiters_And_Next_Rental_Retries()
    {
        var startupGate = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var factory = new FakeRunnerFactory
        {
            BeforeCreate = async (attempt, _) =>
            {
                if (attempt == 1)
                {
                    await startupGate.Task;
                    throw new InvalidOperationException("mongod failed to start");
                }
            },
        };

        using var pool = CreatePool(factory);

        var rentalTask1 = pool.RentAsync(this.CancellationToken);
        var rentalTask2 = pool.RentAsync(this.CancellationToken);
        startupGate.SetResult(true);

        await Assert.ThrowsAsync<InvalidOperationException>(() => rentalTask1);
        await Assert.ThrowsAsync<InvalidOperationException>(() => rentalTask2);

        var runner = await pool.RentAsync(this.CancellationToken);
        Assert.Equal("fake://1", runner.ConnectionString);
        Assert.Equal(2, factory.AttemptCount);
    }

    [Fact]
    public async Task RentAsync_Throws_When_Token_Is_Already_Canceled()
    {
        var factory = new FakeRunnerFactory();
        using var pool = CreatePool(factory);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pool.RentAsync(new CancellationToken(canceled: true)));
        Assert.Equal(0, factory.AttemptCount);
    }

    [Fact]
    public async Task Canceling_A_Rental_Does_Not_Cancel_The_Startup_Shared_With_Other_Rentals()
    {
        var startupGate = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var factory = new FakeRunnerFactory { BeforeCreate = (_, _) => startupGate.Task };
        var delay = new ManualDelay();
        using var pool = CreatePool(factory, delay: delay);

        using var cts = new CancellationTokenSource();
        var canceledRentalTask = pool.RentAsync(cts.Token);
        var rentalTask = pool.RentAsync(this.CancellationToken);

        cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => canceledRentalTask);

        startupGate.SetResult(true);
        var runner = await rentalTask;

        Assert.Equal("fake://1", runner.ConnectionString);
        Assert.Equal(1, factory.CreatedCount);
        Assert.Equal(0, delay.PendingCount);
    }

    [Fact]
    public async Task Runner_Still_Starting_When_All_Rentals_Are_Canceled_Is_Stopped_After_Idle_Timeout()
    {
        var startupGate = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var factory = new FakeRunnerFactory { BeforeCreate = (_, _) => startupGate.Task };
        var delay = new ManualDelay();
        using var pool = CreatePool(factory, delay: delay);

        using var cts = new CancellationTokenSource();
        var rentalTask = pool.RentAsync(cts.Token);
        cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => rentalTask);

        delay.ElapseAll();
        startupGate.SetResult(true);

        await AssertEventuallyAsync(() => factory.CreatedCount == 1 && factory.Runners[0].IsDisposed);

        var runner = await pool.RentAsync(this.CancellationToken);
        Assert.Equal("fake://2", runner.ConnectionString);
    }

    [Fact]
    public async Task Disposing_A_Rented_Runner_Returns_It_To_The_Pool()
    {
        var factory = new FakeRunnerFactory();
        using var pool = CreatePool(factory, maxRentalsPerRunner: 1);

        var runner = await pool.RentAsync(this.CancellationToken);
        runner.Dispose();

        await AssertEventuallyAsync(() => factory.Runners[0].IsDisposed);
    }

    [Fact]
    public async Task Returning_A_Runner_Multiple_Times_Has_No_Effect()
    {
        var factory = new FakeRunnerFactory();
        var delay = new ManualDelay();
        using var pool = CreatePool(factory, delay: delay);

        var runner1 = await pool.RentAsync(this.CancellationToken);
        _ = await pool.RentAsync(this.CancellationToken);

        pool.Return(runner1);
        pool.Return(runner1);
        runner1.Dispose();

        // The second rental is still active, so the runner must not be considered idle
        Assert.Equal(0, delay.PendingCount);
    }

    [Fact]
    public async Task Returned_Runner_Cannot_Be_Used_Anymore()
    {
        var factory = new FakeRunnerFactory();
        using var pool = CreatePool(factory);

        var runner = await pool.RentAsync(this.CancellationToken);
        pool.Return(runner);

        Assert.Throws<ObjectDisposedException>(() => runner.ConnectionString);
        Assert.Throws<ObjectDisposedException>(() => runner.Import("db", "col", "file.json", cancellationToken: this.CancellationToken));
        Assert.Throws<ObjectDisposedException>(() => runner.Export("db", "col", "file.json", cancellationToken: this.CancellationToken));
        await Assert.ThrowsAsync<ObjectDisposedException>(() => runner.ImportAsync("db", "col", "file.json", cancellationToken: this.CancellationToken));
        await Assert.ThrowsAsync<ObjectDisposedException>(() => runner.ExportAsync("db", "col", "file.json", cancellationToken: this.CancellationToken));
    }

    [Fact]
    public async Task Rented_Runner_Forwards_Calls_To_Underlying_Runner()
    {
        var factory = new FakeRunnerFactory();
        using var pool = CreatePool(factory);

        var runner = await pool.RentAsync(this.CancellationToken);
        runner.Import("db", "col", "in.json", cancellationToken: this.CancellationToken);
        await runner.ImportAsync("db", "col", "in.json", cancellationToken: this.CancellationToken);
        runner.Export("db", "col", "out.json", cancellationToken: this.CancellationToken);
        await runner.ExportAsync("db", "col", "out.json", cancellationToken: this.CancellationToken);

        Assert.Equal<string>(["import:db.col", "import:db.col", "export:db.col", "export:db.col"], factory.Runners[0].Calls);
    }

    [Fact]
    public void Return_Throws_When_Runner_Is_Null()
    {
        using var pool = CreatePool(new FakeRunnerFactory());

        Assert.Throws<ArgumentNullException>(() => pool.Return(null!));
    }

    [Fact]
    public async Task Return_Throws_When_Runner_Was_Not_Rented_From_This_Pool()
    {
        using var pool = CreatePool(new FakeRunnerFactory());
        using var otherPool = CreatePool(new FakeRunnerFactory());

        var otherPoolRunner = await otherPool.RentAsync(this.CancellationToken);

        Assert.Throws<ArgumentException>(() => pool.Return(new FakeMongoRunner(42)));
        Assert.Throws<ArgumentException>(() => pool.Return(otherPoolRunner));
    }

    [Fact]
    public async Task Dispose_Stops_All_Runners_Including_Rented_Ones()
    {
        var factory = new FakeRunnerFactory();
        var pool = CreatePool(factory, maxRentalsPerRunner: 1);

        var runner1 = await pool.RentAsync(this.CancellationToken);
        _ = await pool.RentAsync(this.CancellationToken);
        pool.Return(await pool.RentAsync(this.CancellationToken));

        pool.Dispose();

        Assert.Equal(3, factory.CreatedCount);
        await AssertEventuallyAsync(() => factory.Runners.All(x => x.IsDisposed));
        Assert.All(factory.Runners, x => Assert.Equal(1, x.DisposeCount));
        Assert.Throws<ObjectDisposedException>(() => runner1.ConnectionString);
    }

    [Fact]
    public async Task Dispose_Is_Idempotent_And_Return_After_Dispose_Has_No_Effect()
    {
        var factory = new FakeRunnerFactory();
        var pool = CreatePool(factory);
        var runner = await pool.RentAsync(this.CancellationToken);

        pool.Dispose();
        pool.Dispose();
        pool.Return(runner);
        runner.Dispose();

        Assert.Equal(1, factory.Runners[0].DisposeCount);
    }

    [Fact]
    public async Task Rent_Throws_After_Dispose()
    {
        var pool = CreatePool(new FakeRunnerFactory());
        pool.Dispose();

        await Assert.ThrowsAsync<ObjectDisposedException>(() => pool.RentAsync(this.CancellationToken));
        Assert.Throws<ObjectDisposedException>(() => pool.Rent(this.CancellationToken));
    }

    [Fact]
    public async Task Dispose_Aborts_Pending_Rentals()
    {
        var factory = new FakeRunnerFactory { BeforeCreate = (_, cancellationToken) => Task.Delay(Timeout.Infinite, cancellationToken) };
        var pool = CreatePool(factory);

        var rentalTask = pool.RentAsync(this.CancellationToken);
        pool.Dispose();

        await Assert.ThrowsAsync<ObjectDisposedException>(() => rentalTask);
        Assert.Equal(0, factory.CreatedCount);
    }

    [Fact]
    public async Task Errors_While_Stopping_Runners_Are_Logged_Instead_Of_Thrown()
    {
        var logs = new List<string>();
        var factory = new FakeRunnerFactory { ThrowOnDispose = true };
        var pool = new MongoRunnerPool(factory.CreateAsync, maxRentalsPerRunner: 1, TimeSpan.FromSeconds(10), new ManualDelay().DelayAsync, x =>
        {
            lock (logs)
            {
                logs.Add(x);
            }
        });

        pool.Return(await pool.RentAsync(this.CancellationToken));
        _ = await pool.RentAsync(this.CancellationToken);
        pool.Dispose();

        await AssertEventuallyAsync(() =>
        {
            lock (logs)
            {
                return logs.Count == 2;
            }
        });
    }

    private static MongoRunnerPool CreatePool(FakeRunnerFactory factory, int maxRentalsPerRunner = 100, ManualDelay? delay = null)
    {
        return new MongoRunnerPool(factory.CreateAsync, maxRentalsPerRunner, TimeSpan.FromSeconds(10), (delay ?? new ManualDelay()).DelayAsync, errorLogger: null);
    }

    private static async Task AssertEventuallyAsync(Func<bool> condition)
    {
        var timeout = DateTime.UtcNow + TimeSpan.FromSeconds(5);

        while (!condition())
        {
            if (DateTime.UtcNow > timeout)
            {
                Assert.Fail("The condition was not met in time");
            }

            await Task.Delay(10, TestContext.Current.CancellationToken);
        }
    }

    private sealed class FakeRunnerFactory
    {
        private readonly List<FakeMongoRunner> _runners = [];
        private int _attemptCount;

        // Receives the 1-based startup attempt number
        public Func<int, CancellationToken, Task>? BeforeCreate { get; set; }

        public bool ThrowOnDispose { get; set; }

        public int AttemptCount => Volatile.Read(ref this._attemptCount);

        public int CreatedCount
        {
            get
            {
                lock (this._runners)
                {
                    return this._runners.Count;
                }
            }
        }

        public IReadOnlyList<FakeMongoRunner> Runners
        {
            get
            {
                lock (this._runners)
                {
                    return [.. this._runners];
                }
            }
        }

        public async Task<IMongoRunner> CreateAsync(CancellationToken cancellationToken)
        {
            var attempt = Interlocked.Increment(ref this._attemptCount);

            if (this.BeforeCreate != null)
            {
                await this.BeforeCreate(attempt, cancellationToken);
            }

            lock (this._runners)
            {
                var runner = new FakeMongoRunner(this._runners.Count + 1) { ThrowOnDispose = this.ThrowOnDispose };
                this._runners.Add(runner);
                return runner;
            }
        }
    }

    private sealed class FakeMongoRunner(int id) : IMongoRunner
    {
        private int _disposeCount;

        public string ConnectionString { get; } = $"fake://{id}";

        public bool ThrowOnDispose { get; init; }

        public List<string> Calls { get; } = [];

        public int DisposeCount => Volatile.Read(ref this._disposeCount);

        public bool IsDisposed => this.DisposeCount > 0;

        public Task ImportAsync(string database, string collection, string inputFilePath, string[]? additionalArguments = null, bool drop = false, CancellationToken cancellationToken = default)
        {
            this.Import(database, collection, inputFilePath, additionalArguments, drop, cancellationToken);
            return Task.CompletedTask;
        }

        public void Import(string database, string collection, string inputFilePath, string[]? additionalArguments = null, bool drop = false, CancellationToken cancellationToken = default)
        {
            this.Calls.Add($"import:{database}.{collection}");
        }

        public Task ExportAsync(string database, string collection, string outputFilePath, string[]? additionalArguments = null, CancellationToken cancellationToken = default)
        {
            this.Export(database, collection, outputFilePath, additionalArguments, cancellationToken);
            return Task.CompletedTask;
        }

        public void Export(string database, string collection, string outputFilePath, string[]? additionalArguments = null, CancellationToken cancellationToken = default)
        {
            this.Calls.Add($"export:{database}.{collection}");
        }

        public void Dispose()
        {
            Interlocked.Increment(ref this._disposeCount);

            if (this.ThrowOnDispose)
            {
                throw new InvalidOperationException("Failed to stop mongod");
            }
        }
    }

    // Idle delays only complete when the test says so
    private sealed class ManualDelay
    {
        private readonly List<TaskCompletionSource<bool>> _pending = [];
        private readonly List<TimeSpan> _requestedDelays = [];

        public int PendingCount
        {
            get
            {
                lock (this._pending)
                {
                    return this._pending.Count;
                }
            }
        }

        public IReadOnlyList<TimeSpan> RequestedDelays
        {
            get
            {
                lock (this._pending)
                {
                    return [.. this._requestedDelays];
                }
            }
        }

        public Task DelayAsync(TimeSpan delay, CancellationToken cancellationToken)
        {
            var tcs = new TaskCompletionSource<bool>();
            cancellationToken.Register(() => tcs.TrySetCanceled(cancellationToken));

            lock (this._pending)
            {
                this._pending.Add(tcs);
                this._requestedDelays.Add(delay);
            }

            return tcs.Task;
        }

        public void ElapseAll()
        {
            TaskCompletionSource<bool>[] pending;

            lock (this._pending)
            {
                pending = [.. this._pending];
                this._pending.Clear();
            }

            foreach (var tcs in pending)
            {
                tcs.TrySetResult(true);
            }
        }
    }
}