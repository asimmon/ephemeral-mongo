using System.Runtime.InteropServices;

namespace Askaiser.EphemeralMongo.Core;

internal sealed class MongoExecutableLocator : IMongoExecutableLocator
{
    public string? FindMongoExecutablePath()
    {
        var mongoExecutableFileName = GetMongoExecutableFileName();
        var potentialMongoExecutablePaths = GetPotentialMongoExecutablePaths(mongoExecutableFileName);
        return potentialMongoExecutablePaths.Where(x => x.Exists).Select(x => x.FullName).FirstOrDefault();
    }

    private static string GetMongoExecutableFileName()
    {
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            return "mongod.exe";
        }

        if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
        {
            return "mongod";
        }

        if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
        {
            return "mongod";
        }

        throw new NotSupportedException("Current operating system is not supported");
    }

    // https://stackoverflow.com/questions/52797/how-do-i-get-the-path-of-the-assembly-the-code-is-in
    private static IEnumerable<FileInfo> GetPotentialMongoExecutablePaths(string mongoExecutableFileName)
    {
        var relativeMongodPath = Path.Combine("tools", "mongodb", "bin", mongoExecutableFileName);

        yield return new FileInfo(Path.Combine(AppContext.BaseDirectory, relativeMongodPath));

        yield return new FileInfo(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, relativeMongodPath));

        var uri = new UriBuilder(typeof(MongoExecutableLocator).Assembly.CodeBase);
        if (Path.GetDirectoryName(Uri.UnescapeDataString(uri.Path)) is { } path)
        {
            yield return new FileInfo(Path.Combine(path, relativeMongodPath));
        }
    }
}