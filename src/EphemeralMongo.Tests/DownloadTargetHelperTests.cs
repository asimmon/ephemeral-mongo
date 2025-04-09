using System.Runtime.InteropServices;
using System.Text;
using EphemeralMongo.Download;

namespace EphemeralMongo.Tests;

public sealed class DownloadTargetHelperTests : IDisposable
{
    private readonly string _tmpOsReleasePath;
    private readonly DownloadTargetHelper _helper;

    public DownloadTargetHelperTests()
    {
        this._tmpOsReleasePath = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());
        this._helper = new DownloadTargetHelper(this._tmpOsReleasePath);
    }

    [Fact]
    public void GetTarget_ReturnsWindows_WhenOsPlatformIsWindows()
    {
        var target = this._helper.GetTarget(OSPlatform.Windows);
        Assert.Equal("windows", target);
    }

    [Fact]
    public void GetTarget_ReturnsMacOS_WhenOsPlatformIsOSX()
    {
        var target = this._helper.GetTarget(OSPlatform.OSX);
        Assert.Equal("macos", target);
    }

    [Theory]
    [InlineData("ubuntu", "18.04", "ubuntu1804")]
    [InlineData("ubuntu", "18.10", "ubuntu1804")]
    [InlineData("ubuntu", "19.04", "ubuntu1804")]
    [InlineData("ubuntu", "19.10", "ubuntu1804")]
    [InlineData("ubuntu", "20.04", "ubuntu2004")]
    [InlineData("ubuntu", "20.10", "ubuntu2004")]
    [InlineData("ubuntu", "21.04", "ubuntu2004")]
    [InlineData("ubuntu", "21.10", "ubuntu2004")]
    [InlineData("ubuntu", "22.04", "ubuntu2204")]
    [InlineData("ubuntu", "22.10", "ubuntu2204")]
    [InlineData("ubuntu", "23.04", "ubuntu2204")]
    [InlineData("ubuntu", "23.10", "ubuntu2204")]
    [InlineData("ubuntu", "24.04", "ubuntu2404")]
    [InlineData("debian", "10", "debian10")]
    [InlineData("debian", "10.5", "debian10")]
    [InlineData("debian", "11", "debian11")]
    [InlineData("debian", "11.6", "debian11")]
    [InlineData("debian", "12", "debian12")]
    [InlineData("debian", "12.1", "debian12")]
    [InlineData("opensuse-leap", "15.1", "suse15")]
    [InlineData("opensuse-leap", "15.3", "suse15")]
    [InlineData("opensuse-leap", "15.5", "suse15")]
    [InlineData("opensuse", "12.5", "suse12")]
    [InlineData("rhel", "7.0", "rhel70")]
    [InlineData("rhel", "7.1", "rhel70")]
    [InlineData("rhel", "7.2", "rhel72")]
    [InlineData("rhel", "7.5", "rhel72")]
    [InlineData("rhel", "7.9", "rhel72")]
    [InlineData("rhel", "8.0", "rhel8")]
    [InlineData("rhel", "8.1", "rhel81")]
    [InlineData("rhel", "8.2", "rhel81")]
    [InlineData("rhel", "8.3", "rhel83")]
    [InlineData("rhel", "8.5", "rhel83")]
    [InlineData("rhel", "9.0", "rhel90")]
    [InlineData("rhel", "9.1", "rhel90")]
    [InlineData("rhel", "9.2", "rhel90")]
    [InlineData("rhel", "9.3", "rhel93")]
    [InlineData("amzn", "2", "amazon2")]
    [InlineData("amzn", "2023", "amazon2023")]
    public void GetTarget_ReturnsLinuxTarget_WhenLinuxOsReleaseFileExists(string actualId, string actualVersionId, string expectedTarget)
    {
        this.CreateOsReleaseFile(actualId, actualVersionId);
        var target = this._helper.GetTarget(OSPlatform.Linux);
        Assert.Equal(expectedTarget, target);
    }

    [Theory]
    [InlineData("ubuntu", "16.04")]
    [InlineData("ubuntu", "17.10")]
    [InlineData("debian", "9")]
    [InlineData("debian", "9.13")]
    [InlineData("opensuse-leap", "11.4")]
    [InlineData("rhel", "6.10")]
    [InlineData("rhel", "6.0")]
    [InlineData("amzn", "1")]
    [InlineData("amzn", "2022")]
    public void GetTarget_ThrowsNotSupportedException_WhenOsVersionIsInvalid(string id, string versionId)
    {
        this.CreateOsReleaseFile(id, versionId);
        var exception = Assert.Throws<NotSupportedException>(() => this._helper.GetTarget(OSPlatform.Linux));
        Assert.Contains(versionId, exception.Message);
    }

    [Fact]
    public void GetTarget_ReturnsFallbackTarget_WhenOsReleaseFileDoesNotExist()
    {
        var target = this._helper.GetTarget(OSPlatform.Linux);
        Assert.Equal("ubuntu2204", target);
    }

    [Theory]
    [InlineData("ubuntu")]
    [InlineData("debian")]
    [InlineData("opensuse-leap")]
    [InlineData("rhel")]
    [InlineData("amzn")]
    public void GetTarget_ReturnsFallbackTarget_WhenVersionIdIsMissing(string id)
    {
        this.CreateOsReleaseFile(id, versionId: null);
        var target = this._helper.GetTarget(OSPlatform.Linux);
        Assert.Equal("ubuntu2204", target);
    }

    [Theory]
    [InlineData("18.04")]
    [InlineData("20.04")]
    [InlineData("22.04")]
    [InlineData("10")]
    [InlineData("11")]
    [InlineData("15.3")]
    [InlineData("9.3")]
    [InlineData("2023")]
    public void GetTarget_ReturnsFallbackTarget_WhenIdIsMissing(string versionId)
    {
        this.CreateOsReleaseFile(id: null, versionId);
        var target = this._helper.GetTarget(OSPlatform.Linux);
        Assert.Equal("ubuntu2204", target);
    }

    [Fact]
    public void GetTarget_ReturnsFallbackTarget_WhenBothIdAndVersionIdAreMissing()
    {
        this.CreateOsReleaseFile(id: null, versionId: null);
        var target = this._helper.GetTarget(OSPlatform.Linux);
        Assert.Equal("ubuntu2204", target);
    }

    [Theory]
    [InlineData("fedora", "38")]
    [InlineData("centos", "9")]
    [InlineData("arch", "rolling")]
    [InlineData("manjaro", "23.1.0")]
    [InlineData("alpine", "3.18")]
    public void GetTarget_ReturnsFallbackTarget_WhenIdIsNotSupported(string id, string versionId)
    {
        this.CreateOsReleaseFile(id, versionId);
        var target = this._helper.GetTarget(OSPlatform.Linux);
        Assert.Equal("ubuntu2204", target);
    }

#if NET9_0_OR_GREATER
    [Fact]
    public void GetTarget_ThrowsPlatformNotSupportedException_WhenOsPlatformIsUnsupported()
    {
        var unsupportedPlatform = OSPlatform.FreeBSD;
        Assert.Throws<PlatformNotSupportedException>(() => this._helper.GetTarget(unsupportedPlatform));
    }
#endif

    private void CreateOsReleaseFile(string? id, string? versionId)
    {
        var sb = new StringBuilder();

        sb.AppendLine("NAME=\"Not a real OS\"");

        if (id != null)
        {
            sb.AppendLine($"ID=\"{id}\"");
        }

        sb.AppendLine("PRETTY_NAME=\"Not a real OS\"");

        if (versionId != null)
        {
            sb.AppendLine($"VERSION_ID=\"{versionId}\"");
        }

        sb.AppendLine("FOO=\"bar\"");

        File.WriteAllText(this._tmpOsReleasePath, sb.ToString());
    }

    public void Dispose()
    {
        try
        {
            File.Delete(this._tmpOsReleasePath);
        }
        catch (IOException)
        {
            // File will be cleaned up by the OS at some point
        }
    }
}
