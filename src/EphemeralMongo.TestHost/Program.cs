using EphemeralMongo;
using EphemeralMongo.Tests;

// Started and killed by KillOnParentExitIntegrationTests: <data directory> <mongod PID file path> [--watchdog]
var dataDirectory = args[0];
var pidFilePath = args[1];

if (args.Contains("--watchdog"))
{
    KillOnParentExit.StrategyOverride = new WatchdogKillOnParentExitStrategy();
}

using var runner = await MongoRunner.RunAsync(new MongoRunnerOptions
{
    DataDirectory = dataDirectory,
    ConnectionTimeout = MongoTestOptions.ConnectionTimeout,
    AdditionalArguments = [.. MongoTestOptions.CommunityArguments, "--pidfilepath", pidFilePath],
    StandardErrorLogger = Console.Error.WriteLine,
});

Console.WriteLine("READY");

await Task.Delay(Timeout.Infinite);
