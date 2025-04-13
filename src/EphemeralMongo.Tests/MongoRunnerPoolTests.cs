using MongoDB.Bson;
using MongoDB.Driver;

#pragma warning disable EMEX0001 // Type is for evaluation purposes only

namespace EphemeralMongo.Tests;

public class MongoRunnerPoolTests(ITestContextAccessor testContextAccessor)
{
    [Fact]
    public async Task RentAsync_Returns_New_Runner_When_Pool_Is_Empty()
    {
        // Arrange
        var options = CreateDefaultOptions();

        // Act
        using var pool = new MongoRunnerPool(options);
        using var runner = await pool.RentAsync(testContextAccessor.Current.CancellationToken);

        // Assert
        Assert.NotNull(runner);
        Assert.NotNull(runner.ConnectionString);
        Assert.Contains("mongodb://127.0.0.1", runner.ConnectionString);

        // Verify server is running
        this.PingServerSuccessful(runner.ConnectionString);
    }

    [Fact]
    public void Rent_Returns_New_Runner_When_Pool_Is_Empty()
    {
        // Arrange
        var options = CreateDefaultOptions();

        // Act
        using var pool = new MongoRunnerPool(options);
        using var runner = pool.Rent(testContextAccessor.Current.CancellationToken);

        // Assert
        Assert.NotNull(runner);
        Assert.NotNull(runner.ConnectionString);
        Assert.Contains("mongodb://127.0.0.1", runner.ConnectionString);

        // Verify server is running
        this.PingServerSuccessful(runner.ConnectionString);
    }

    [Fact]
    public async Task RentAsync_Reuses_Runner_When_Returned_And_Not_MaxedOut()
    {
        // Arrange
        var options = CreateDefaultOptions();
        using var pool = new MongoRunnerPool(options, 2);

        // Act
        var runner1 = await pool.RentAsync(testContextAccessor.Current.CancellationToken);
        var runner2 = await pool.RentAsync(testContextAccessor.Current.CancellationToken);

        var connectionString1 = runner1.ConnectionString;
        var connectionString2 = runner2.ConnectionString;

        pool.Return(runner1);

        // Assert
        Assert.Equal(connectionString1, connectionString2);
        Assert.Same(runner1, runner2);

        // Verify server is still running
        Assert.ThrowsAny<TimeoutException>(() => this.PingServerSuccessful(connectionString1));

        // Cleanup
        pool.Return(runner2);
    }

    [Fact]
    public void Rent_Reuses_Runner_When_Returned_And_Not_MaxedOut()
    {
        // Arrange
        var options = CreateDefaultOptions();
        using var pool = new MongoRunnerPool(options, 2);

        // Act
        var runner1 = pool.Rent(testContextAccessor.Current.CancellationToken);
        var runner2 = pool.Rent(testContextAccessor.Current.CancellationToken);

        var connectionString1 = runner1.ConnectionString;
        var connectionString2 = runner2.ConnectionString;

        pool.Return(runner1);

        // Assert
        Assert.Equal(connectionString1, connectionString2);
        Assert.Same(runner1, runner2);

        Assert.ThrowsAny<TimeoutException>(() => this.PingServerSuccessful(connectionString1));

        // Cleanup
        pool.Return(runner2);
    }

    [Fact]
    public async Task RentAsync_Creates_New_Runner_When_MaxRentals_Reached()
    {
        // Arrange
        var options = CreateDefaultOptions();
        using var pool = new MongoRunnerPool(options, 1);

        // Act
        var runner1 = await pool.RentAsync(testContextAccessor.Current.CancellationToken);
        var connectionString1 = runner1.ConnectionString;
        pool.Return(runner1);

        var runner2 = await pool.RentAsync(testContextAccessor.Current.CancellationToken);
        var connectionString2 = runner2.ConnectionString;

        // Assert
        Assert.NotEqual(connectionString1, connectionString2);
        Assert.NotSame(runner1, runner2);

        // Verify server is running for the new runner
        this.PingServerSuccessful(runner2.ConnectionString);

        // Cleanup
        pool.Return(runner2);
    }

    [Fact]
    public void Rent_Creates_New_Runner_When_MaxRentals_Reached()
    {
        // Arrange
        var options = CreateDefaultOptions();
        using var pool = new MongoRunnerPool(options, 1);

        // Act
        var runner1 = pool.Rent(testContextAccessor.Current.CancellationToken);
        var connectionString1 = runner1.ConnectionString;
        pool.Return(runner1);

        var runner2 = pool.Rent(testContextAccessor.Current.CancellationToken);
        var connectionString2 = runner2.ConnectionString;

        // Assert
        Assert.NotEqual(connectionString1, connectionString2);
        Assert.NotSame(runner1, runner2);

        // Verify server is running for the new runner
        this.PingServerSuccessful(runner2.ConnectionString);

        // Cleanup
        pool.Return(runner2);
    }

    [Fact]
    public void Return_Throws_When_Runner_Not_From_Pool()
    {
        // Arrange
        var options = CreateDefaultOptions();
        using var pool = new MongoRunnerPool(options);
        using var externalRunner = MongoRunner.Run(options, testContextAccessor.Current.CancellationToken);

        // Act & Assert
        Assert.Throws<ArgumentException>(() => pool.Return(externalRunner));
    }

    [Fact]
    public async Task Return_Disposes_Runner_When_MaxRentals_Reached()
    {
        // Arrange
        var options = CreateDefaultOptions();
        using var pool = new MongoRunnerPool(options, 1);

        // Act
        var runner = await pool.RentAsync(testContextAccessor.Current.CancellationToken);
        var connectionString = runner.ConnectionString;

        // Verify the server is running before returning
        this.PingServerSuccessful(connectionString);

        // Return the runner - it will be marked for disposal because it reached max rentals (1)
        pool.Return(runner);

        // Rent again - should get a new runner since we've maxed rentals at 1
        var runner2 = await pool.RentAsync(testContextAccessor.Current.CancellationToken);

        // At this point, the original runner should be disposed
        // Trying to use it should throw a connection exception
        Assert.ThrowsAny<TimeoutException>(() => this.PingServerSuccessful(connectionString));

        // But the new runner should be working
        this.PingServerSuccessful(runner2.ConnectionString);

        // Cleanup
        pool.Return(runner2);
    }

    [Fact]
    public void Dispose_Disposes_All_Runners()
    {
        // Arrange
        var options = CreateDefaultOptions();
        var pool = new MongoRunnerPool(options);

        // Act
        var runner1 = pool.Rent(testContextAccessor.Current.CancellationToken);
        var runner2 = pool.Rent(testContextAccessor.Current.CancellationToken);
        var connectionString1 = runner1.ConnectionString;
        var connectionString2 = runner2.ConnectionString;

        // Assert clients connect successfully before disposal
        this.PingServerSuccessful(connectionString1);
        this.PingServerSuccessful(connectionString2);

        // Dispose the pool without returning the runners
        pool.Dispose();

        // Assert clients cannot connect anymore
        Assert.ThrowsAny<TimeoutException>(() => this.PingServerSuccessful(connectionString1));
        Assert.ThrowsAny<TimeoutException>(() => this.PingServerSuccessful(connectionString2));
    }

    [Fact]
    public async Task Rent_Throws_ObjectDisposedException_When_Disposed()
    {
        // Arrange
        var options = CreateDefaultOptions();
        var pool = new MongoRunnerPool(options);
        pool.Dispose();

        // Act & Assert
        await Assert.ThrowsAsync<ObjectDisposedException>(() =>
            pool.RentAsync(testContextAccessor.Current.CancellationToken));
        Assert.Throws<ObjectDisposedException>(() => pool.Rent(testContextAccessor.Current.CancellationToken));
    }

    [Fact]
    public void Return_Throws_ObjectDisposedException_When_Disposed()
    {
        // Arrange
        var options = CreateDefaultOptions();
        var pool = new MongoRunnerPool(options);
        var runner = pool.Rent(testContextAccessor.Current.CancellationToken);
        pool.Dispose();

        // Act & Assert
        Assert.Throws<ObjectDisposedException>(() => pool.Return(runner));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void Constructor_Throws_When_MaxRentalsPerRunner_LessThan_One(int maxRentals)
    {
        // Arrange
        var options = CreateDefaultOptions();

        // Act & Assert
        Assert.Throws<ArgumentOutOfRangeException>(() => new MongoRunnerPool(options, maxRentals));
    }

    [Fact]
    public void Constructor_Throws_When_Options_Is_Null()
    {
        // Act & Assert
        Assert.Throws<ArgumentNullException>(() => new MongoRunnerPool(null!));
    }

    [Fact]
    public void Pool_Creates_MaxRentalsPerRunner_Distinct_Instances()
    {
        // Arrange
        const int maxRentalsPerRunner = 25;
        const int totalRentals = 100;
        const int expectedDistinctInstances = 4;

        var options = CreateDefaultOptions();
        using var pool = new MongoRunnerPool(options, maxRentalsPerRunner);
        var connectionStrings = new HashSet<string>(StringComparer.Ordinal);

        // Act
        for (var i = 0; i < totalRentals; i++)
        {
            var runner = pool.Rent(testContextAccessor.Current.CancellationToken);
            connectionStrings.Add(runner.ConnectionString);
        }

        // Assert
        Assert.Equal(expectedDistinctInstances, connectionStrings.Count);
    }

    [Fact]
    public void PooledRunner_Dispose_DoesNothing_But_Return_DisposesWhenMaxed()
    {
        // Arrange
        var options = CreateDefaultOptions();
        using var pool = new MongoRunnerPool(options, maxRentalsPerRunner: 2);
        var runner = pool.Rent(testContextAccessor.Current.CancellationToken);
        var connectionString = runner.ConnectionString;

        // Act 1: Dispose the pooled runner should have no effect
        runner.Dispose();
        this.PingServerSuccessful(connectionString);

        // Act 2: Once returned and no more rentals
        pool.Return(runner);

        // Assert 2: Server is no longer accessible
        Assert.ThrowsAny<TimeoutException>(() => this.PingServerSuccessful(connectionString));
    }

    private void PingServerSuccessful(string connectionString)
    {
        GetAdminDatabase(connectionString).RunCommand<BsonDocument>(new BsonDocument("ping", 1), cancellationToken: testContextAccessor.Current.CancellationToken);
    }

    private static MongoRunnerOptions CreateDefaultOptions() => new();

    private static IMongoDatabase GetAdminDatabase(string connectionString)
    {
        var clientSettings = MongoClientSettings.FromConnectionString(connectionString);

        clientSettings.ConnectTimeout = TimeSpan.FromSeconds(1);
        clientSettings.HeartbeatTimeout = TimeSpan.FromSeconds(1);
        clientSettings.SocketTimeout = TimeSpan.FromSeconds(1);
        clientSettings.ServerSelectionTimeout = TimeSpan.FromSeconds(1);

        return new MongoClient(clientSettings).GetDatabase("admin");
    }
}
