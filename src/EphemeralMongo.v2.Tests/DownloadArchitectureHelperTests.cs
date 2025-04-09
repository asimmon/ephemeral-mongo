using System.Runtime.InteropServices;
using EphemeralMongo.Download;

namespace EphemeralMongo.Tests;

public sealed class DownloadArchitectureHelperTests
{
    [Fact]
    public void GetArchitecture_ReturnsCurrentArchitecture()
    {
        var result = DownloadArchitectureHelper.GetArchitecture();
        var expected = DownloadArchitectureHelper.GetArchitecture(RuntimeInformation.ProcessArchitecture, RuntimeInformationHelper.OSPlatform);
        Assert.Equal(expected, result);
    }

    [Theory]
    [InlineData(Architecture.X86, "x86_64")]
    [InlineData(Architecture.X64, "x86_64")]
    public void GetArchitecture_ReturnsX86_64_ForX86AndX64(Architecture architecture, string expectedResult)
    {
        var result = DownloadArchitectureHelper.GetArchitecture(architecture, OSPlatform.Windows);
        Assert.Equal(expectedResult, result);
    }

    [Fact]
    public void GetArchitecture_ReturnsAarch64_ForArm64OnNonMacOS()
    {
        var result = DownloadArchitectureHelper.GetArchitecture(Architecture.Arm64, OSPlatform.Linux);
        Assert.Equal("aarch64", result);
    }

    [Fact]
    public void GetArchitecture_ReturnsArm64_ForArm64OnMacOS()
    {
        var result = DownloadArchitectureHelper.GetArchitecture(Architecture.Arm64, OSPlatform.OSX);
        Assert.Equal("arm64", result);
    }

    [Theory]
    [InlineData(Architecture.Arm)]
    [InlineData(Architecture.Wasm)]
    [InlineData(Architecture.S390x)]
    [InlineData(Architecture.LoongArch64)]
    [InlineData(Architecture.Armv6)]
    [InlineData(Architecture.Ppc64le)]
    [InlineData((Architecture)99)] // Some undefined architecture
    public void GetArchitecture_ThrowsPlatformNotSupportedException_ForUnsupportedArchitectures(Architecture architecture)
    {
        var exception = Assert.Throws<PlatformNotSupportedException>(() =>
            DownloadArchitectureHelper.GetArchitecture(architecture, OSPlatform.Windows));
        Assert.Contains(architecture.ToString(), exception.Message);
    }
}
