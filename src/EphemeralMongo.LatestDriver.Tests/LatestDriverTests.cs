using MongoDB.Bson;
using MongoDB.Driver;

namespace EphemeralMongo.Tests;

public class LatestDriverTests(ITestOutputHelper testOutputHelper, ITestContextAccessor testContextAccessor)
{
    // The replica set setup is where the library relies the most on the driver (cluster events and registry)
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task StartMongo_WithLatestDriver_Succeeds(bool useSingleNodeReplicaSet)
    {
        var options = new MongoRunnerOptions
        {
            UseSingleNodeReplicaSet = useSingleNodeReplicaSet,
            StandardOutputLogger = testOutputHelper.WriteLine,
            StandardErrorLogger = testOutputHelper.WriteLine,
            ConnectionTimeout = MongoTestOptions.ConnectionTimeout,
            ReplicaSetSetupTimeout = MongoTestOptions.ReplicaSetSetupTimeout,
            AdditionalArguments = MongoTestOptions.CommunityArguments,
        };

        using var runner = await MongoRunner.RunAsync(options, testContextAccessor.Current.CancellationToken);
        using var client = new MongoClient(runner.ConnectionString);

        var collection = client.GetDatabase("default").GetCollection<BsonDocument>("smoke");
        await collection.InsertOneAsync(new BsonDocument("name", "John Doe"), options: null, testContextAccessor.Current.CancellationToken);

        var count = await collection.CountDocumentsAsync(FilterDefinition<BsonDocument>.Empty, options: null, testContextAccessor.Current.CancellationToken);
        Assert.Equal(1, count);
    }
}
