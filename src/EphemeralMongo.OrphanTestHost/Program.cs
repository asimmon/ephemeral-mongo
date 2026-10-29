using EphemeralMongo;

// Starts mongod the way a test fixture does, reports readiness, then waits to be killed by the test
using var runner = await MongoRunner.RunAsync(new MongoRunnerOptions
{
    UseSingleNodeReplicaSet = args.Contains("--replset"),
    ConnectionTimeout = TimeSpan.FromMinutes(2),
    ReplicaSetSetupTimeout = TimeSpan.FromMinutes(2),
    AdditionalArguments = ["--quiet", "--wiredTigerCacheSizeGB", "0.25", "--setParameter", "diagnosticDataCollectionEnabled=false"],
}).ConfigureAwait(false);

await Console.Out.WriteLineAsync("READY").ConfigureAwait(false);
await Console.Out.FlushAsync().ConfigureAwait(false);
await Task.Delay(Timeout.Infinite).ConfigureAwait(false);
