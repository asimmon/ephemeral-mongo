namespace EphemeralMongo.Tests;

public class ProcessArgumentTests
{
    [Theory]
    [InlineData("", "\"\"")]
    [InlineData("/", "/")]
    [InlineData("\\", "\\")]
    [InlineData("foo", "foo")]
    [InlineData("/foo", "/foo")]
    [InlineData("c:\\foo", "c:\\foo")]
    [InlineData("\\foo", "\\foo")]
    [InlineData("foo bar", "\"foo bar\"")]
    [InlineData("/foo/hello world", "\"/foo/hello world\"")]
    [InlineData("c:\\foo\\hello world", "\"c:\\foo\\hello world\"")]
    [InlineData("\\\"", "\"\\\\\\\"\"")]
    [InlineData("fo\"ob\"a \\\\r", "\"fo\\\"ob\\\"a \\\\r\"")]
    public void Nothing(string inputPath, string expectedEscapedPath)
    {
        Assert.Equal(expectedEscapedPath, ProcessArgument.Escape(inputPath));
    }
}