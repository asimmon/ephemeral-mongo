using EphemeralMongo.Download;

namespace EphemeralMongo.Tests;

public sealed class DownloadReleaseHelperTests
{
    [Theory]
    [InlineData("8.0.4", "8.0.4")]
    [InlineData("9.0.0", "9.0.0")]
    [InlineData("100.13.0", "100.13.0")]
    public void GetVersionDirectoryName_ReturnsNormalizedVersion_ForValidVersions(string version, string expectedResult)
    {
        var result = DownloadReleaseHelper.GetVersionDirectoryName(version);
        Assert.Equal(expectedResult, result);
    }

    [Theory]
    [InlineData("")]
    [InlineData("8.")]
    [InlineData("8./../../evil")]
    [InlineData("8.0.4/../../evil")]
    [InlineData("..")]
    [InlineData("8.0.4-rc0")]
    public void GetVersionDirectoryName_Throws_ForInvalidVersions(string version)
    {
        Assert.Throws<EphemeralMongoException>(() => DownloadReleaseHelper.GetVersionDirectoryName(version));
    }

    [Theory]
    [InlineData("https://fastdl.mongodb.org/windows/mongodb-windows-x86_64-8.0.4.zip", "mongodb-windows-x86_64-8.0.4.zip")]
    [InlineData("https://fastdl.mongodb.org/linux/mongodb-linux-x86_64-ubuntu2404-8.0.4.tgz", "mongodb-linux-x86_64-ubuntu2404-8.0.4.tgz")]
    [InlineData("https://downloads.mongodb.org/tools/db/mongodb-database-tools-macos-arm64-100.13.0.zip", "mongodb-database-tools-macos-arm64-100.13.0.zip")]
    [InlineData("https://downloads.mongodb.com/linux/mongodb-linux-x86_64-enterprise-ubuntu2204-9.0.4.tgz", "mongodb-linux-x86_64-enterprise-ubuntu2204-9.0.4.tgz")]
    [InlineData("https://FASTDL.MONGODB.ORG/osx/mongodb-macos-arm64-8.0.4.TGZ", "mongodb-macos-arm64-8.0.4.TGZ")]
    [InlineData("https://fastdl.mongodb.org/a/../mongodb-windows-x86_64-8.0.4.zip", "mongodb-windows-x86_64-8.0.4.zip")]
    public void GetArchiveFileName_ReturnsFileName_ForMongoDbHttpsUrls(string url, string expectedResult)
    {
        var result = DownloadReleaseHelper.GetArchiveFileName(url);
        Assert.Equal(expectedResult, result);
    }

    [Theory]
    [InlineData("")]
    [InlineData("mongodb-windows-x86_64-8.0.4.zip")]
    [InlineData("http://fastdl.mongodb.org/windows/mongodb-windows-x86_64-8.0.4.zip")]
    [InlineData("ftp://fastdl.mongodb.org/windows/mongodb-windows-x86_64-8.0.4.zip")]
    [InlineData("file:///C:/mongodb-windows-x86_64-8.0.4.zip")]
    [InlineData("https://evil.example/mongodb-windows-x86_64-8.0.4.zip")]
    [InlineData("https://mongodb.org.evil.example/mongodb-windows-x86_64-8.0.4.zip")]
    [InlineData("https://evilmongodb.org/mongodb-windows-x86_64-8.0.4.zip")]
    [InlineData("https://mongodb.com.evil.example/mongodb-windows-x86_64-8.0.4.zip")]
    [InlineData("https://evilmongodb.com/mongodb-windows-x86_64-8.0.4.zip")]
    [InlineData("https://fastdl.mongodb.org@evil.example/mongodb-windows-x86_64-8.0.4.zip")]
    [InlineData("https://fastdl.mongodb.org/windows/")]
    [InlineData("https://fastdl.mongodb.org/windows/mongodb-windows-x86_64-8.0.4.exe")]
    [InlineData("https://fastdl.mongodb.org/windows/c:mongodb-windows-x86_64-8.0.4.zip")]
    public void GetArchiveFileName_Throws_ForUnexpectedUrls(string url)
    {
        Assert.Throws<EphemeralMongoException>(() => DownloadReleaseHelper.GetArchiveFileName(url));
    }
}
