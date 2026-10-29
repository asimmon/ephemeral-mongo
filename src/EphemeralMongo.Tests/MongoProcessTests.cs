using System.ComponentModel;

namespace EphemeralMongo.Tests;

public class MongoProcessTests(ITestContextAccessor testContextAccessor)
{
    [Fact]
    public async Task StartAsync_WithNonExistentExecutable_DisposeDoesNotHideStartupException()
    {
        var executablePath = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString(), "mongoimport");

        await Assert.ThrowsAsync<Win32Exception>(async () =>
        {
            using (var process = new MongoImportExportProcess(new MongoRunnerOptions(), executablePath, ["--version"]))
            {
                await process.StartAsync(testContextAccessor.Current.CancellationToken);
            }
        });
    }

    [Fact]
    public void Dispose_WithoutStartingProcess_DoesNotThrow()
    {
        var executablePath = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString(), "mongod");

        var process = new MongodProcess(new MongoRunnerOptions(), executablePath, []);
        process.Dispose();
    }
}
