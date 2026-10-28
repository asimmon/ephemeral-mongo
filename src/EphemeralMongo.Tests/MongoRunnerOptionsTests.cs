namespace EphemeralMongo.Tests;

public class MongoRunnerOptionsTests
{
    [Fact]
    public void Directories_CanBeResetToNull()
    {
        var options = new MongoRunnerOptions
        {
            DataDirectory = "data",
            BinaryDirectory = "bin",
        };

        options.DataDirectory = null;
        options.BinaryDirectory = null;

        Assert.Null(options.DataDirectory);
        Assert.Null(options.BinaryDirectory);
    }

    [Fact]
    public void DataDirectory_InvalidPath_ThrowsWithPropertyNameAsParamName()
    {
        var options = new MongoRunnerOptions();

        var ex = Assert.Throws<ArgumentException>(() => options.DataDirectory = string.Empty);

        Assert.Equal(nameof(MongoRunnerOptions.DataDirectory), ex.ParamName);
        Assert.NotNull(ex.InnerException);
    }

    [Fact]
    public void DataDirectoryLifetime_AcceptsNullAndRejectsNegative()
    {
        var options = new MongoRunnerOptions
        {
            DataDirectoryLifetime = null,
        };

        Assert.Null(options.DataDirectoryLifetime);
        Assert.Throws<ArgumentOutOfRangeException>(() => options.DataDirectoryLifetime = TimeSpan.FromSeconds(-1));
    }
}
