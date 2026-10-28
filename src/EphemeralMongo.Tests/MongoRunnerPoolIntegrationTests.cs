using System.Net;
using System.Net.Sockets;
using MongoDB.Bson;
using MongoDB.Driver;

#pragma warning disable EMEX0001 // Type is for evaluation purposes only
#pragma warning disable CA1849 // Synchronous APIs are tested on purpose

namespace EphemeralMongo.Tests;

[Trait(XunitConstants.Category, XunitConstants.SlowOnWindows)]
public class MongoRunnerPoolIntegrationTests(ITestOutputHelper testOutputHelper, ITestContextAccessor testContextAccessor)
{
    private CancellationToken CancellationToken => testContextAccessor.Current.CancellationToken;

    [Fact]
    public async Task Rentals_Share_The_Same_Mongod_Process()
    {
        using var pool = new MongoRunnerPool(this.CreateOptions());

        var runner1 = await pool.RentAsync(this.CancellationToken);
        var runner2 = pool.Rent(this.CancellationToken);

        var connectionString = runner1.ConnectionString;
        Assert.Equal(connectionString, runner2.ConnectionString);
        this.AssertServerUp(connectionString);

        pool.Return(runner1);
        pool.Return(runner2);

        // Still alive during the idle period
        this.AssertServerUp(connectionString);
    }

    [Fact]
    public async Task Retired_Mongod_Process_Is_Stopped_Once_All_Its_Rentals_Are_Returned()
    {
        using var pool = new MongoRunnerPool(this.CreateOptions(), maxRentalsPerRunner: 2);

        var runner1 = await pool.RentAsync(this.CancellationToken);
        var runner2 = await pool.RentAsync(this.CancellationToken);
        var runner3 = await pool.RentAsync(this.CancellationToken);

        var retiredConnectionString = runner1.ConnectionString;
        Assert.NotEqual(retiredConnectionString, runner3.ConnectionString);

        pool.Return(runner1);
        this.AssertServerUp(retiredConnectionString);

        pool.Return(runner2);
        await AssertServerDownAsync(retiredConnectionString);
        this.AssertServerUp(runner3.ConnectionString);
    }

    [Fact]
    public async Task Dispose_Stops_Rented_Mongod_Processes()
    {
        var pool = new MongoRunnerPool(this.CreateOptions());
        var runner = await pool.RentAsync(this.CancellationToken);
        var connectionString = runner.ConnectionString;

        pool.Dispose();

        await AssertServerDownAsync(connectionString);
    }

    private MongoRunnerOptions CreateOptions() => new()
    {
        StandardOutputLogger = this.MongoMessageLogger,
        StandardErrorLogger = this.MongoMessageLogger,
        ConnectionTimeout = MongoTestOptions.ConnectionTimeout,
        AdditionalArguments = MongoTestOptions.CommunityArguments,
    };

    private void MongoMessageLogger(string message)
    {
        try
        {
            testOutputHelper.WriteLine(message);
        }
        catch
        {
            // Ignored, the test might have already completed
        }
    }

    private void AssertServerUp(string connectionString)
    {
        var admin = new MongoClient(connectionString).GetDatabase("admin");
        admin.RunCommand<BsonDocument>(new BsonDocument("ping", 1), cancellationToken: this.CancellationToken);
    }

    private static async Task AssertServerDownAsync(string connectionString)
    {
        var port = new MongoUrl(connectionString).Server.Port;
        var timeout = DateTime.UtcNow + TimeSpan.FromSeconds(10);

        while (true)
        {
            var listener = new TcpListener(IPAddress.Loopback, port);

            try
            {
                // The port is available, so the server is down
                listener.Start();
                return;
            }
            catch (SocketException) when (DateTime.UtcNow < timeout)
            {
                await Task.Delay(100, TestContext.Current.CancellationToken);
            }
            catch (SocketException ex)
            {
                Assert.Fail($"Server is still running on port {port}. Exception: {ex}");
            }
            finally
            {
                listener.Stop();
            }
        }
    }
}
