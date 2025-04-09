using System.Runtime.InteropServices;
using EphemeralMongo.Download;

namespace EphemeralMongo.Tests;

public sealed class DownloadEditionHelperTests
{
    [Fact]
    public void GetEdition_WithEditionOnly_ReturnsCorrectEdition()
    {
        var edition = MongoEdition.Enterprise;
        var result = DownloadEditionHelper.GetEdition(edition);
        var expected = DownloadEditionHelper.GetEdition(edition, RuntimeInformationHelper.OSPlatform);

        Assert.Equal(expected, result);
    }

    [Fact]
    public void GetEdition_EnterpriseEdition_AlwaysReturnsEnterprise()
    {
        // Enterprise edition should return "enterprise" regardless of platform
        var result1 = DownloadEditionHelper.GetEdition(MongoEdition.Enterprise, OSPlatform.Windows);
        var result2 = DownloadEditionHelper.GetEdition(MongoEdition.Enterprise, OSPlatform.OSX);
        var result3 = DownloadEditionHelper.GetEdition(MongoEdition.Enterprise, OSPlatform.Linux);

        Assert.Equal("enterprise", result1);
        Assert.Equal("enterprise", result2);
        Assert.Equal("enterprise", result3);
    }

    [Fact]
    public void GetEdition_CommunityEdition_ReturnsBaseOrTargeted()
    {
        // Community edition should return "targeted" for Linux, "base" for other platforms
        var result1 = DownloadEditionHelper.GetEdition(MongoEdition.Community, OSPlatform.Windows);
        var result2 = DownloadEditionHelper.GetEdition(MongoEdition.Community, OSPlatform.OSX);
        var result3 = DownloadEditionHelper.GetEdition(MongoEdition.Community, OSPlatform.Linux);

        Assert.Equal("base", result1);
        Assert.Equal("base", result2);
        Assert.Equal("targeted", result3);
    }
}
