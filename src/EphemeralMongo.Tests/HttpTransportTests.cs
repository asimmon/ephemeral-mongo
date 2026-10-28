using System.Net;
#if NETFRAMEWORK
using System.Net.Http;
#endif
using System.Text.Json.Serialization;

namespace EphemeralMongo.Tests;

public class HttpTransportTests(ITestContextAccessor testContextAccessor)
{
    private const string Url = "https://downloads.example.test/file";

    [Fact]
    public async Task GetFromJsonAsync_WithTransientStatusCode_Retries()
    {
        var handler = new FakeHttpMessageHandler(
            _ => new HttpResponseMessage(HttpStatusCode.ServiceUnavailable),
            _ => new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("""{ "name": "foo" }""") });

        var result = await new HttpTransport(handler).GetFromJsonAsync<NameDto>(Url, testContextAccessor.Current.CancellationToken);

        Assert.Equal("foo", result.Name);
        Assert.Equal(2, handler.RequestCount);
    }

    [Fact]
    public async Task GetFromJsonAsync_WithNonTransientStatusCode_DoesNotRetry()
    {
        var handler = new FakeHttpMessageHandler(_ => new HttpResponseMessage(HttpStatusCode.NotFound));

        var ex = await Assert.ThrowsAsync<EphemeralMongoException>(() => new HttpTransport(handler).GetFromJsonAsync<NameDto>(Url, testContextAccessor.Current.CancellationToken));

        Assert.Contains("404", ex.ToString());
        Assert.Equal(1, handler.RequestCount);
    }

    [Fact]
    public async Task DownloadFileAsync_WithNetworkError_RetriesAndWritesFile()
    {
        var handler = new FakeHttpMessageHandler(
            _ => throw new HttpRequestException("Connection reset"),
            _ => new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent([1, 2, 3]) });

        var filePath = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());

        try
        {
            await new HttpTransport(handler).DownloadFileAsync(Url, filePath, testContextAccessor.Current.CancellationToken);

#pragma warning disable CA1849 // File.ReadAllBytesAsync isn't available on .NET Framework
            Assert.Equal([1, 2, 3], File.ReadAllBytes(filePath));
#pragma warning restore CA1849
            Assert.Equal(2, handler.RequestCount);
        }
        finally
        {
            File.Delete(filePath);
        }
    }

    [Fact]
    public async Task DownloadFileAsync_WithPersistentNetworkError_ThrowsAfterMaxAttempts()
    {
        var handler = new FakeHttpMessageHandler(_ => throw new HttpRequestException("Connection reset"));
        var filePath = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());

        try
        {
            var ex = await Assert.ThrowsAsync<EphemeralMongoException>(() => new HttpTransport(handler).DownloadFileAsync(Url, filePath, testContextAccessor.Current.CancellationToken));

            Assert.IsType<HttpRequestException>(ex.InnerException);
            Assert.Equal(3, handler.RequestCount);
        }
        finally
        {
            File.Delete(filePath);
        }
    }

    private sealed class NameDto
    {
        [JsonPropertyName("name")]
        public string Name { get; set; } = string.Empty;
    }

    // Each request is answered by the next response factory, the last one being reused for any additional request
    private sealed class FakeHttpMessageHandler(params Func<HttpRequestMessage, HttpResponseMessage>[] responseFactories) : HttpMessageHandler
    {
        public int RequestCount { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var responseFactory = responseFactories[Math.Min(this.RequestCount, responseFactories.Length - 1)];
            this.RequestCount++;
            return Task.FromResult(responseFactory(request));
        }
    }
}
