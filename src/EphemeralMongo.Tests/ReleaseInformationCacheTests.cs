using System.Net;
#if NETFRAMEWORK
using System.Net.Http;
#endif
using EphemeralMongo.Download;

namespace EphemeralMongo.Tests;

#pragma warning disable CA1849 // File.ReadAllTextAsync and File.WriteAllTextAsync aren't available on .NET Framework
public sealed class ReleaseInformationCacheTests(ITestContextAccessor testContextAccessor) : IDisposable
{
    private readonly string _cacheDirPath = Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), Path.GetRandomFileName())).FullName;

    private string CacheFilePath => Path.Combine(this._cacheDirPath, "release.json");

    private CancellationToken CancellationToken => testContextAccessor.Current.CancellationToken;

    [Fact]
    public async Task GetAsync_WithoutCache_DownloadsAndCaches()
    {
        var handler = CreateHandler("foo");

        var (versions, _) = await this.GetAsync(handler, TimeSpan.FromDays(1));

        Assert.Equal("foo", versions.Versions[0].Version);
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

        var (versions, actualFetchedAtUtc) = await this.GetAsync(handler, TimeSpan.FromDays(1));

        Assert.Equal("cached", versions.Versions[0].Version);
        Assert.Equal(fetchedAtUtc, actualFetchedAtUtc);
        Assert.Equal(0, handler.RequestCount);
    }

    [Fact]
    public async Task GetAsync_WithStaleCache_DownloadsAgain()
    {
        this.WriteCache("cached", DateTime.UtcNow.AddDays(-2));
        var handler = CreateHandler("foo");

        var startedAtUtc = DateTime.UtcNow;
        var (versions, fetchedAtUtc) = await this.GetAsync(handler, TimeSpan.FromDays(1));

        Assert.Equal("foo", versions.Versions[0].Version);
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

        var (versions, _) = await this.GetAsync(handler, TimeSpan.FromDays(7));

        Assert.Equal("foo", versions.Versions[0].Version);
        Assert.Equal(1, handler.RequestCount);
    }

    [Theory]
    [InlineData("")]
    [InlineData("null")]
    [InlineData("""{ "name": "trunc""")]
    [InlineData("{}")]
    [InlineData("""{ "versions": [] }""")]
    [InlineData("""{ "versions": null }""")]
    public async Task GetAsync_WithInvalidCache_DownloadsAgain(string cacheContent)
    {
        File.WriteAllText(this.CacheFilePath, cacheContent);
        var handler = CreateHandler("foo");

        var (versions, _) = await this.GetAsync(handler, TimeSpan.FromDays(1));

        Assert.Equal("foo", versions.Versions[0].Version);
        Assert.Equal(1, handler.RequestCount);
        Assert.Contains("foo", File.ReadAllText(this.CacheFilePath));
    }

    [Fact]
    public async Task GetAsync_WithInvalidResponseAndStaleCache_ReturnsStaleCache()
    {
        this.WriteCache("cached", DateTime.UtcNow.AddDays(-2));
        var handler = new FakeHttpMessageHandler(_ => new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("<html>Service Unavailable</html>") });
        var logs = new List<string>();

        var (versions, fetchedAtUtc) = await this.GetAsync(handler, TimeSpan.FromDays(1), logs.Add);

        Assert.Equal("cached", versions.Versions[0].Version);
        Assert.Null(fetchedAtUtc);

        // The actual cause is logged rather than the outer exception mentioning the deleted temporary file
        var log = Assert.Single(logs);
        Assert.Contains(ReleaseInformationCache.MongodReleasesUrl, log);
        Assert.Contains("invalid", log);
        Assert.DoesNotContain(".tmp", log);

        Assert.Contains("cached", File.ReadAllText(this.CacheFilePath));
        this.AssertNoTemporaryFiles();
    }

    [Fact]
    public async Task GetAsync_WithUnusableResponseAndStaleCache_ReturnsStaleCacheWithoutReplacingIt()
    {
        this.WriteCache("cached", DateTime.UtcNow.AddDays(-2));
        var handler = new FakeHttpMessageHandler(_ => new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("{}") });
        var logs = new List<string>();

        var (versions, fetchedAtUtc) = await this.GetAsync(handler, TimeSpan.FromDays(1), logs.Add);

        Assert.Equal("cached", versions.Versions[0].Version);
        Assert.Null(fetchedAtUtc);
        Assert.Contains("doesn't contain any release information", Assert.Single(logs));
        Assert.Contains("cached", File.ReadAllText(this.CacheFilePath));
    }

    [Fact]
    public async Task GetAsync_WithUnusableResponseWithoutCache_Throws()
    {
        var handler = new FakeHttpMessageHandler(_ => new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("{}") });

        var ex = await Assert.ThrowsAsync<EphemeralMongoException>(() => this.GetAsync(handler, TimeSpan.FromDays(1)));

        Assert.IsType<InvalidDataException>(ex.InnerException);
        Assert.False(File.Exists(this.CacheFilePath));
    }

    [Fact]
    public async Task GetAsync_WithFailedRequestAndStaleCache_ReturnsStaleCache()
    {
        var lastWriteTimeUtc = DateTime.UtcNow.AddDays(-2);
        this.WriteCache("cached", lastWriteTimeUtc);
        var handler = new FakeHttpMessageHandler(_ => new HttpResponseMessage(HttpStatusCode.NotFound));
        var logs = new List<string>();

        var (versions, fetchedAtUtc) = await this.GetAsync(handler, TimeSpan.FromDays(1), logs.Add);

        Assert.Equal("cached", versions.Versions[0].Version);
        Assert.Null(fetchedAtUtc);
        Assert.Equal(1, handler.RequestCount);
        Assert.Contains("404", Assert.Single(logs));

        // The stale cache must not look fresh to the next caller, which should try downloading again
        Assert.Equal(lastWriteTimeUtc, File.GetLastWriteTimeUtc(this.CacheFilePath));
        this.AssertNoTemporaryFiles();
    }

    [Fact]
    public async Task GetAsync_AfterRecentFailedRequest_ReturnsStaleCacheWithoutSendingRequest()
    {
        this.WriteCache("cached", DateTime.UtcNow.AddDays(-2));
        var handler = new FakeHttpMessageHandler(_ => new HttpResponseMessage(HttpStatusCode.NotFound));
        var logs = new List<string>();

        await this.GetAsync(handler, TimeSpan.FromDays(1));
        var (versions, fetchedAtUtc) = await this.GetAsync(handler, TimeSpan.FromDays(1), logs.Add);

        Assert.Equal("cached", versions.Versions[0].Version);
        Assert.Null(fetchedAtUtc);
        Assert.Equal(1, handler.RequestCount);
        Assert.Contains("failed less than", Assert.Single(logs));
    }

    [Fact]
    public async Task GetAsync_AfterOldFailedRequest_DownloadsAgainAndForgetsTheFailure()
    {
        this.WriteCache("cached", DateTime.UtcNow.AddDays(-2));
        var failedMarkerFilePath = this.CacheFilePath + ".failed";
        File.WriteAllText(failedMarkerFilePath, string.Empty);
        File.SetLastWriteTimeUtc(failedMarkerFilePath, DateTime.UtcNow.AddMinutes(-10));
        var handler = CreateHandler("foo");

        var (versions, _) = await this.GetAsync(handler, TimeSpan.FromDays(1));

        Assert.Equal("foo", versions.Versions[0].Version);
        Assert.Equal(1, handler.RequestCount);
        Assert.False(File.Exists(failedMarkerFilePath));
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

        await Assert.ThrowsAnyAsync<Exception>(() => ReleaseInformationCache.GetMongodVersionsAsync(CreateOptions(handler, TimeSpan.FromDays(1)), this.CacheFilePath, cts.Token));

        this.AssertNoTemporaryFiles();
    }

    [Fact]
    public async Task GetAsync_WithConcurrentCallers_SendsSingleRequest()
    {
        var handler = CreateHandler("foo", responseDelay: TimeSpan.FromMilliseconds(200));

        var results = await Task.WhenAll(Enumerable.Range(0, 20).Select(_ => Task.Run(() => this.GetAsync(handler, TimeSpan.FromDays(1)), this.CancellationToken)));

        Assert.All(results, x => Assert.Equal("foo", x.Versions.Versions[0].Version));
        Assert.Equal(1, handler.RequestCount);
    }

    [Fact]
    public async Task GetAsync_WhileAnotherProcessIsDownloading_WaitsAndUsesItsResult()
    {
        var handler = CreateHandler("foo");

        // Holding the lock file directly simulates another process, as the in-process mutex isn't involved
        Task<(MongoVersionsDto Versions, DateTime? FetchedAtUtc)> getTask;
        using (await FileLock.AcquireAsync(this.CacheFilePath + ".lock", this.CancellationToken))
        {
            getTask = this.GetAsync(handler, TimeSpan.FromDays(1));

            await Task.Delay(TimeSpan.FromMilliseconds(500), this.CancellationToken);
            Assert.False(getTask.IsCompleted, "The cache should not be read while another process holds the lock.");

            this.WriteCache("other-process", DateTime.UtcNow);
        }

        var (versions, _) = await getTask;

        Assert.Equal("other-process", versions.Versions[0].Version);
        Assert.Equal(0, handler.RequestCount);
    }

    [Fact]
    public async Task GetAsync_WhileAnotherProcessHoldsTheLockForTooLong_ReturnsStaleCache()
    {
        this.WriteCache("cached", DateTime.UtcNow.AddDays(-2));
        var handler = CreateHandler("foo");
        var logs = new List<string>();

        // Tests of the same class don't run in parallel, so changing this shared value is safe
        var originalLockWaitTime = ReleaseInformationCache.StaleCopyLockWaitTime;
        ReleaseInformationCache.StaleCopyLockWaitTime = TimeSpan.FromMilliseconds(300);

        try
        {
            using (await FileLock.AcquireAsync(this.CacheFilePath + ".lock", this.CancellationToken))
            {
                var (versions, fetchedAtUtc) = await this.GetAsync(handler, TimeSpan.FromDays(1), logs.Add);

                Assert.Equal("cached", versions.Versions[0].Version);
                Assert.Null(fetchedAtUtc);
            }
        }
        finally
        {
            ReleaseInformationCache.StaleCopyLockWaitTime = originalLockWaitTime;
        }

        Assert.Equal(0, handler.RequestCount);
        Assert.Contains("too long", Assert.Single(logs));
    }

    [Fact]
    public async Task GetAsync_WithLeftoverTemporaryFiles_DeletesThem()
    {
        this.WriteCache("cached", DateTime.UtcNow);
        File.WriteAllText(this.CacheFilePath + ".abcdefgh.ijk.tmp", "{ \"na");

        await this.GetAsync(CreateHandler("foo"), TimeSpan.FromDays(1));

        this.AssertNoTemporaryFiles();
    }

    [Fact]
    public async Task GetToolsVersionsAsync_WithoutCache_DownloadsAndCaches()
    {
        var handler = CreateHandler("100.0.0");

        var (versions, _) = await ReleaseInformationCache.GetToolsVersionsAsync(CreateOptions(handler, TimeSpan.FromDays(1)), this.CacheFilePath, this.CancellationToken);

        Assert.Equal("100.0.0", Assert.Single(versions.Versions).Version);
        Assert.Contains("100.0.0", File.ReadAllText(this.CacheFilePath));
    }

    [Fact]
    public async Task GetToolsVersionsAsync_WithUnusableResponse_Throws()
    {
        var handler = new FakeHttpMessageHandler(_ => new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("""{ "versions": [] }""") });

        await Assert.ThrowsAsync<EphemeralMongoException>(() => ReleaseInformationCache.GetToolsVersionsAsync(CreateOptions(handler, TimeSpan.FromDays(1)), this.CacheFilePath, this.CancellationToken));

        Assert.False(File.Exists(this.CacheFilePath));
    }

    public void Dispose()
    {
        Directory.Delete(this._cacheDirPath, recursive: true);
    }

    private static FakeHttpMessageHandler CreateHandler(string version, TimeSpan responseDelay = default)
    {
        return new FakeHttpMessageHandler(_ => new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(CreateDocument(version)) })
        {
            ResponseDelay = responseDelay,
        };
    }

    private static string CreateDocument(string version) => $$"""{ "versions": [{ "version": "{{version}}" }] }""";

    private static MongoRunnerOptions CreateOptions(FakeHttpMessageHandler handler, TimeSpan maxAge, Logger? logger = null) => new()
    {
        Transport = new HttpTransport(handler),
        NewVersionCheckTimeout = maxAge,
        StandardErrorLogger = logger,
    };

    private Task<(MongoVersionsDto Versions, DateTime? FetchedAtUtc)> GetAsync(FakeHttpMessageHandler handler, TimeSpan maxAge, Logger? logger = null)
    {
        return ReleaseInformationCache.GetMongodVersionsAsync(CreateOptions(handler, maxAge, logger), this.CacheFilePath, this.CancellationToken);
    }

    private void WriteCache(string version, DateTime lastWriteTimeUtc)
    {
        File.WriteAllText(this.CacheFilePath, CreateDocument(version));
        File.SetLastWriteTimeUtc(this.CacheFilePath, lastWriteTimeUtc);
    }

    private void AssertNoTemporaryFiles()
    {
        Assert.Empty(Directory.GetFiles(this._cacheDirPath, "*.tmp"));
    }
}
#pragma warning restore CA1849
