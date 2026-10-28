#if NETFRAMEWORK
using System.Net.Http;
#endif

namespace EphemeralMongo.Tests;

// Each request is answered by the next response factory, the last one being reused for any additional request
internal sealed class FakeHttpMessageHandler(params Func<HttpRequestMessage, HttpResponseMessage>[] responseFactories) : HttpMessageHandler
{
    private int _requestCount;

    public int RequestCount => Volatile.Read(ref this._requestCount);

    // Useful for making concurrent requests overlap
    public TimeSpan ResponseDelay { get; init; }

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var requestIndex = Interlocked.Increment(ref this._requestCount) - 1;
        var responseFactory = responseFactories[Math.Min(requestIndex, responseFactories.Length - 1)];

        if (this.ResponseDelay > TimeSpan.Zero)
        {
            await Task.Delay(this.ResponseDelay, cancellationToken);
        }

        return responseFactory(request);
    }
}
