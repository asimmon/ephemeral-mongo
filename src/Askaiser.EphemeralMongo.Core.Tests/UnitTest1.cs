using System.Diagnostics;
using Microsoft.Extensions.Logging;
using ShareGate.Extensions.Xunit;
using Xunit;
using Xunit.Abstractions;

namespace Askaiser.EphemeralMongo.Core.Tests;

public class UnitTest1 : BaseIntegrationTest
{
    public UnitTest1(ITestOutputHelper testOutputHelper)
        : base(testOutputHelper)
    {
    }

    [Fact]
    public async Task Test1()
    {
        var sw = Stopwatch.StartNew();

        using var runner = MongoRunner.Run(new MongoRunnerOptions
        {
            // MongoDirectory = @"/home/asimmon/Downloads/mongodb-linux-x86_64-ubuntu2004-5.0.9/bin",
            // UseSingleNodeReplicaSet = true,
        });

        sw.Stop();

        this.Logger.LogInformation("{MongoConnectionString} is ready in {Elapsed} seconds", runner.ConnectionString, sw.Elapsed.TotalSeconds);
    }
}