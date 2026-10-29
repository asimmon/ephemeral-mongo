namespace EphemeralMongo.Tests;

public sealed class AppDataDirectoryTests
{
    [Fact]
    public void EnsureRootExists_RestrictsRootToOwner_OnUnix()
    {
#if NET8_0_OR_GREATER
        if (OperatingSystem.IsWindows())
        {
            Assert.Skip("The local app data directory is already only accessible to its owner on Windows");
        }
        else
        {
            AppDataDirectory.EnsureRootExists();

            var unixFileMode = File.GetUnixFileMode(AppDataDirectory.RootPath);
            Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute, unixFileMode);
        }
#else
        Assert.Skip("Unix file modes can only be read on .NET 8 and later");
#endif
    }
}
