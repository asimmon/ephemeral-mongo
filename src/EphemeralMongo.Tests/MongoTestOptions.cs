namespace EphemeralMongo.Tests;

internal static class MongoTestOptions
{
    // CI runners can be slow to start mongod while other test assemblies download, extract and start MongoDB instances in parallel
    public static readonly TimeSpan ConnectionTimeout = TimeSpan.FromMinutes(2);

    // By default, each mongod sizes its cache to ~50% of the machine's memory, which adds up when tests start many instances in parallel.
    // Diagnostic data collection (FTDC) is disabled to save disk and CPU, as nobody looks at it.
    public static readonly string[] CommunityArguments = ["--quiet", "--wiredTigerCacheSizeGB", "0.25", "--setParameter", "diagnosticDataCollectionEnabled=false"];

    public static readonly string[] EnterpriseInMemoryArguments = ["--quiet", "--storageEngine", "inMemory", "--inMemorySizeGB", "0.25", "--setParameter", "diagnosticDataCollectionEnabled=false"];
}
