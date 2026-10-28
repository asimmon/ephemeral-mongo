using System.Net;
#if NETFRAMEWORK
using System.Net.Http;
#endif
using System.Text.Json.Serialization;
using EphemeralMongo.Download;

namespace EphemeralMongo.Tests;

#pragma warning disable CA1849 // File.ReadAllTextAsync and File.WriteAllTextAsync aren't available on .NET Framework
public sealed class ReleaseInformationCacheTests(ITestContextAccessor testContextAccessor) : IDisposable
{
    private const string Url = "https://downloads.example.test/release.json";

    private readonly string _cacheDirPath = Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), Path.GetRandomFileName())).FullName;

    private string CacheFilePath => Path.Combine(this._cacheDirPath, "release.json");

    private CancellationToken CancellationToken => testContextAccessor.Current.CancellationToken;

    [Fact]
    public async Task GetAsync_WithoutCache_DownloadsAndCaches()
    {
        var handler = CreateHandler("foo");

        var (value, _) = await this.GetAsync(handler, TimeSpan.FromDays(1));

        Assert.Equal("foo", value.Name);
        Assert.Equal(1, handler.RequestCount);
        Assert.Contains("foo", File.ReadAllText(this.CacheFilePath));
        this.AssertNoTemporaryFiles();
    }

    [Fact]
    public async Task GetAsync_WithFreshCache_DoesNotSendRequest()
    {
        var fetchedAtUtc = DateTime.UtcNow.AddHours(-1);
        this.WriteCache("cached", fetchedAtUtc);
        var handler = CreateHandler("foo");

        var (value, actualFetchedAtUtc) = await this.GetAsync(handler, TimeSpan.FromDays(1));

        Assert.Equal("cached", value.Name);
        Assert.Equal(fetchedAtUtc, actualFetchedAtUtc);
        Assert.Equal(0, handler.RequestCount);
    }

    [Fact]
    public async Task GetAsync_WithStaleCache_DownloadsAgain()
    {
        this.WriteCache("cached", DateTime.UtcNow.AddDays(-2));
        var handler = CreateHandler("foo");

        var startedAtUtc = DateTime.UtcNow;
        var (value, fetchedAtUtc) = await this.GetAsync(handler, TimeSpan.FromDays(1));

        Assert.Equal("foo", value.Name);
        Assert.NotNull(fetchedAtUtc);
        Assert.True(fetchedAtUtc >= startedAtUtc);
        Assert.Equal(1, handler.RequestCount);
        Assert.Contains("foo", File.ReadAllText(this.CacheFilePath));
        this.AssertNoTemporaryFiles();
    }

    [Fact]
    public async Task GetAsync_WithZeroMaxAge_AlwaysDownloads()
    {
        var handler = CreateHandler("foo");

        await this.GetAsync(handler, TimeSpan.Zero);
        await this.GetAsync(handler, TimeSpan.Zero);

        Assert.Equal(2, handler.RequestCount);
    }

    [Fact]
    public async Task GetAsync_WithCacheLastWrittenInTheFuture_DownloadsAgain()
    {
        this.WriteCache("cached", DateTime.UtcNow.AddDays(1));
        var handler = CreateHandler("foo");

        var (value, _) = await this.GetAsync(handler, TimeSpan.FromDays(7));

        Assert.Equal("foo", value.Name);
        Assert.Equal(1, handler.RequestCount);
    }

    [Theory]
    [InlineData("")]
    [InlineData("null")]
    [InlineData("""{ "name": "trunc""")]
    public async Task GetAsync_WithInvalidCache_DownloadsAgain(string cacheContent)
    {
        File.WriteAllText(this.CacheFilePath, cacheContent);
        var handler = CreateHandler("foo");

        var (value, _) = await this.GetAsync(handler, TimeSpan.FromDays(1));

        Assert.Equal("foo", value.Name);
        Assert.Equal(1, handler.RequestCount);
        Assert.Contains("foo", File.ReadAllText(this.CacheFilePath));
    }

    [Fact]
    public async Task GetAsync_WithInvalidResponseAndStaleCache_ReturnsStaleCache()
    {
        this.WriteCache("cached", DateTime.UtcNow.AddDays(-2));
        var handler = new FakeHttpMessageHandler(_ => new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("<html>Service Unavailable</html>") });
        var logs = new List<string>();

        var (value, fetchedAtUtc) = await this.GetAsync(handler, TimeSpan.FromDays(1), logs.Add);

        Assert.Equal("cached", value.Name);
        Assert.Null(fetchedAtUtc);
        Assert.Contains(Url, Assert.Single(logs));
        Assert.Contains("cached", File.ReadAllText(this.CacheFilePath));
        this.AssertNoTemporaryFiles();
    }

    [Fact]
    public async Task GetAsync_WithFailedRequestAndStaleCache_ReturnsStaleCache()
    {
        var lastWriteTimeUtc = DateTime.UtcNow.AddDays(-2);
        this.WriteCache("cached", lastWriteTimeUtc);
        var handler = new FakeHttpMessageHandler(_ => new HttpResponseMessage(HttpStatusCode.NotFound));

        var (value, fetchedAtUtc) = await this.GetAsync(handler, TimeSpan.FromDays(1));

        Assert.Equal("cached", value.Name);
        Assert.Null(fetchedAtUtc);
        Assert.Equal(1, handler.RequestCount);

        // The stale cache must not look fresh to the next caller, which should try downloading again
        Assert.Equal(lastWriteTimeUtc, File.GetLastWriteTimeUtc(this.CacheFilePath));
        this.AssertNoTemporaryFiles();
    }

    [Fact]
    public async Task GetAsync_WithFailedRequestAndInvalidCache_Throws()
    {
        File.WriteAllText(this.CacheFilePath, "{ ");
        var handler = new FakeHttpMessageHandler(_ => new HttpResponseMessage(HttpStatusCode.NotFound));

        await Assert.ThrowsAsync<EphemeralMongoException>(() => this.GetAsync(handler, TimeSpan.FromDays(1)));

        this.AssertNoTemporaryFiles();
    }

    [Fact]
    public async Task GetAsync_WithFailedRequestWithoutCache_Throws()
    {
        var handler = new FakeHttpMessageHandler(_ => new HttpResponseMessage(HttpStatusCode.NotFound));

        await Assert.ThrowsAsync<EphemeralMongoException>(() => this.GetAsync(handler, TimeSpan.FromDays(1)));

        Assert.False(File.Exists(this.CacheFilePath));
        this.AssertNoTemporaryFiles();
    }

    [Fact]
    public async Task GetAsync_WhenCancelledWithStaleCache_Throws()
    {
        this.WriteCache("cached", DateTime.UtcNow.AddDays(-2));
        var handler = CreateHandler("foo", responseDelay: TimeSpan.FromSeconds(30));

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(this.CancellationToken);
        cts.CancelAfter(TimeSpan.FromMilliseconds(200));

        await Assert.ThrowsAnyAsync<Exception>(() => ReleaseInformationCache.GetAsync<NameDto>(new HttpTransport(handler), Url, this.CacheFilePath, TimeSpan.FromDays(1), logger: null, cts.Token));

        this.AssertNoTemporaryFiles();
    }

    [Fact]
    public async Task GetAsync_WithConcurrentCallers_SendsSingleRequest()
    {
        var handler = CreateHandler("foo", responseDelay: TimeSpan.FromMilliseconds(200));

        var results = await Task.WhenAll(Enumerable.Range(0, 20).Select(_ => Task.Run(() => this.GetAsync(handler, TimeSpan.FromDays(1)), this.CancellationToken)));

        Assert.All(results, x => Assert.Equal("foo", x.Value.Name));
        Assert.Equal(1, handler.RequestCount);
    }

    [Fact]
    public async Task GetAsync_WhileAnotherProcessIsDownloading_WaitsAndUsesItsResult()
    {
        var handler = CreateHandler("foo");

        // Holding the lock file directly simulates another process, as the in-process mutex isn't involved
        Task<(NameDto Value, DateTime? FetchedAtUtc)> getTask;
        using (await FileLock.AcquireAsync(this.CacheFilePath + ".lock", this.CancellationToken))
        {
            getTask = this.GetAsync(handler, TimeSpan.FromDays(1));

            await Task.Delay(TimeSpan.FromMilliseconds(500), this.CancellationToken);
            Assert.False(getTask.IsCompleted, "The cache should not be read while another process holds the lock.");

            this.WriteCache("other-process", DateTime.UtcNow);
        }

        var (value, _) = await getTask;

        Assert.Equal("other-process", value.Name);
        Assert.Equal(0, handler.RequestCount);
    }

    public void Dispose()
    {
        Directory.Delete(this._cacheDirPath, recursive: true);
    }

    private static FakeHttpMessageHandler CreateHandler(string name, TimeSpan responseDelay = default)
    {
        return new FakeHttpMessageHandler(_ => new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent($$"""{ "name": "{{name}}" }""") })
        {
            ResponseDelay = responseDelay,
        };
    }

    private Task<(NameDto Value, DateTime? FetchedAtUtc)> GetAsync(FakeHttpMessageHandler handler, TimeSpan maxAge, Logger? logger = null)
    {
        return ReleaseInformationCache.GetAsync<NameDto>(new HttpTransport(handler), Url, this.CacheFilePath, maxAge, logger, this.CancellationToken);
    }

    private void WriteCache(string name, DateTime lastWriteTimeUtc)
    {
        File.WriteAllText(this.CacheFilePath, $$"""{ "name": "{{name}}" }""");
        File.SetLastWriteTimeUtc(this.CacheFilePath, lastWriteTimeUtc);
    }

    private void AssertNoTemporaryFiles()
    {
        Assert.Empty(Directory.GetFiles(this._cacheDirPath, "*.tmp"));
    }

    private sealed class NameDto
    {
        [JsonPropertyName("name")]
        public string Name { get; set; } = string.Empty;
    }
}
#pragma warning restore CA1849
