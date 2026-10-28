#pragma warning disable CA1849 // Async file APIs aren't available on .NET Framework
using System.Diagnostics;
using EphemeralMongo.Download;

namespace EphemeralMongo.Tests;

public sealed class FileCompressionHelperTests : IDisposable
{
    private readonly string _tmpDirPath = Path.Combine(Path.GetTempPath(), "ephemeral-mongo-tests", Path.GetRandomFileName());

    public FileCompressionHelperTests()
    {
        Directory.CreateDirectory(this._tmpDirPath);
    }

    [Fact]
    public async Task ExtractToDirectoryAsync_ValidTarGz_ExtractsFiles()
    {
        var sourceDirPath = Path.Combine(this._tmpDirPath, "source");
        Directory.CreateDirectory(Path.Combine(sourceDirPath, "bin"));
        File.WriteAllText(Path.Combine(sourceDirPath, "bin", "hello.txt"), "world");

        var archiveFilePath = Path.Combine(this._tmpDirPath, "archive.tgz");
        RunTar($"-czf {ProcessArgument.Escape(archiveFilePath)} -C {ProcessArgument.Escape(sourceDirPath)} bin");

        var destinationDirPath = Path.Combine(this._tmpDirPath, "destination");
        Directory.CreateDirectory(destinationDirPath);

        await FileCompressionHelper.ExtractToDirectoryAsync(archiveFilePath, destinationDirPath, TestContext.Current.CancellationToken);

        Assert.Equal("world", File.ReadAllText(Path.Combine(destinationDirPath, "bin", "hello.txt")));
    }

    [Fact]
    public async Task ExtractToDirectoryAsync_InvalidTarGz_ThrowsWithTarOutput()
    {
        var archiveFilePath = Path.Combine(this._tmpDirPath, "invalid.tgz");
        File.WriteAllText(archiveFilePath, "this is not a gzip archive");

        var destinationDirPath = Path.Combine(this._tmpDirPath, "destination");
        Directory.CreateDirectory(destinationDirPath);

        var ex = await Assert.ThrowsAsync<EphemeralMongoException>(() => FileCompressionHelper.ExtractToDirectoryAsync(archiveFilePath, destinationDirPath, TestContext.Current.CancellationToken));

        var innerEx = Assert.IsType<InvalidOperationException>(ex.InnerException);
        Assert.Contains("Output:", innerEx.Message, StringComparison.Ordinal);
    }

    private static void RunTar(string arguments)
    {
        using var process = Process.Start(new ProcessStartInfo("tar", arguments) { UseShellExecute = false, CreateNoWindow = true })!;
        process.WaitForExit();
        Assert.Equal(0, process.ExitCode);
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(this._tmpDirPath, recursive: true);
        }
        catch (IOException)
        {
        }
    }
}
