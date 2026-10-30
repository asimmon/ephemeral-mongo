using EphemeralMongo;
using EphemeralMongo.KillOnParentExit;
using EphemeralMongo.Tests;

// KillOnParentExitIntegrationTests starts this app, waits for it to start mongod, then kills it to check that mongod is killed too.
// Arguments: <data directory> <mongod PID file path> [--watchdog <watchdog log file path>]
var dataDirectory = args[0];
var pidFilePath = args[1];

if (args is [_, _, "--watchdog", var watchdogLogFilePath])
{
    KillOnParentExitStrategies.StrategyOverride = new WatchdogKillOnParentExitStrategy(watchdogLogFilePath);
}

using var runner = await MongoRunner.RunAsync(new MongoRunnerOptions
{
    DataDirectory = dataDirectory,
    ConnectionTimeout = MongoTestOptions.ConnectionTimeout,
    AdditionalArguments = [.. MongoTestOptions.CommunityArguments, "--pidfilepath", pidFilePath],
    StandardErrorLogger = Console.Error.WriteLine,
});

Console.WriteLine("READY");

// The test kills this app within seconds. If the test crashes before that, this app stops mongod and exits by itself instead of running forever.
await Task.Delay(TimeSpan.FromMinutes(2));
