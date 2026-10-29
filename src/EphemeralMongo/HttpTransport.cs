using System.Diagnostics.CodeAnalysis;

namespace EphemeralMongo;

[SuppressMessage("Design", "CA1001:Types that own disposable fields should be disposable", Justification = "HttpClient is a singleton or managed by the caller")]
[SuppressMessage("Maintainability", "CA1510", Justification = "ArgumentNullException.ThrowIfNull isn't worth it when multi-targeting")]
public sealed class HttpTransport
{
#if NET8_0_OR_GREATER
    private static readonly HttpClient SharedDefaultHttpClient = new HttpClient(new SocketsHttpHandler
    {
        PooledConnectionLifetime = TimeSpan.FromMinutes(5)
    });
#else
    private static readonly HttpClient SharedDefaultHttpClient = new HttpClient();
#endif

    private const int MaxAttempts = 3;
    private static readonly TimeSpan InitialRetryDelay = TimeSpan.FromSeconds(1);
    private static readonly TimeSpan ReadInactivityTimeout = TimeSpan.FromSeconds(60);

    private readonly HttpClient _httpClient;

    public HttpTransport(HttpClient httpClient)
    {
        this._httpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
    }

    internal HttpTransport()
        : this(SharedDefaultHttpClient)
    {
    }

    public HttpTransport(HttpMessageHandler handler)
    {
        if (handler == null)
        {
            throw new ArgumentNullException(nameof(handler));
        }

        this._httpClient = new HttpClient(handler);
    }

    internal Task DownloadFileAsync(string url, string filePath, CancellationToken cancellationToken)
    {
        return this.DownloadFileAsync(url, filePath, attemptTimeout: null, cancellationToken);
    }

    // The attempt timeout covers the whole request including the response body, which suits small documents
    internal async Task DownloadFileAsync(string url, string filePath, TimeSpan? attemptTimeout, CancellationToken cancellationToken)
    {
        try
        {
            await ExecuteWithRetryAsync(ct => this.DownloadFileOnceAsync(url, filePath, attemptTimeout, ct), cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            throw new EphemeralMongoException($"An error occurred while downloading the file from {url} to {filePath}", ex);
        }
    }

    private async Task DownloadFileOnceAsync(string url, string filePath, TimeSpan? attemptTimeout, CancellationToken cancellationToken)
    {
        // Cancelling the attempt, but not the caller's token, is retried
        using var attemptCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        if (attemptTimeout.HasValue)
        {
            attemptCts.CancelAfter(attemptTimeout.Value);
        }

        var attemptCancellationToken = attemptCts.Token;

        using var response = await this._httpClient.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, attemptCancellationToken).ConfigureAwait(false);
        EnsureSuccessStatusCode(response);

        // HttpClient.Timeout doesn't apply to reading the response body, so a stalled download would otherwise hang forever
        using var inactivityCts = CancellationTokenSource.CreateLinkedTokenSource(attemptCancellationToken);

        // Disposing the response aborts pending reads on runtimes where stream reads ignore cancellation tokens (.NET Framework)
        using var inactivityRegistration = inactivityCts.Token.Register(response.Dispose);

        const int bufferSize = 81920;
        var buffer = new byte[bufferSize];

#if NET8_0_OR_GREATER
        Stream sourceStream;
        Stream destinationStream;
        await using ((sourceStream = await response.Content.ReadAsStreamAsync(attemptCancellationToken).ConfigureAwait(false)).ConfigureAwait(false))
        await using ((destinationStream = File.Create(filePath)).ConfigureAwait(false))
        {
            int bytesRead;
            inactivityCts.CancelAfter(ReadInactivityTimeout);
            while ((bytesRead = await sourceStream.ReadAsync(buffer.AsMemory(0, bufferSize), inactivityCts.Token).ConfigureAwait(false)) > 0)
            {
                await destinationStream.WriteAsync(buffer.AsMemory(0, bytesRead), attemptCancellationToken).ConfigureAwait(false);
                inactivityCts.CancelAfter(ReadInactivityTimeout);
            }
        }
#else
        using var sourceStream = await response.Content.ReadAsStreamAsync().ConfigureAwait(false);
        using var destinationStream = File.Create(filePath);

        int bytesRead;
        inactivityCts.CancelAfter(ReadInactivityTimeout);
#if NETSTANDARD2_1_OR_GREATER
        while ((bytesRead = await sourceStream.ReadAsync(buffer.AsMemory(0, bufferSize), inactivityCts.Token).ConfigureAwait(false)) > 0)
        {
            await destinationStream.WriteAsync(buffer.AsMemory(0, bytesRead), attemptCancellationToken).ConfigureAwait(false);
            inactivityCts.CancelAfter(ReadInactivityTimeout);
        }
#else
        while ((bytesRead = await sourceStream.ReadAsync(buffer, 0, bufferSize, inactivityCts.Token).ConfigureAwait(false)) > 0)
        {
            await destinationStream.WriteAsync(buffer, 0, bytesRead, attemptCancellationToken).ConfigureAwait(false);
            inactivityCts.CancelAfter(ReadInactivityTimeout);
        }
#endif
#endif
    }

    private static void EnsureSuccessStatusCode(HttpResponseMessage response)
    {
        if (response.IsSuccessStatusCode)
        {
            return;
        }

        var statusCode = (int)response.StatusCode;
        var isTransient = statusCode is 408 or 429 || statusCode >= 500;

        if (isTransient)
        {
            // Throws an HttpRequestException, which is retried
            response.EnsureSuccessStatusCode();
        }

        throw new EphemeralMongoException($"The request failed with status code {statusCode} ({response.ReasonPhrase})");
    }

    private static async Task ExecuteWithRetryAsync(Func<CancellationToken, Task> action, CancellationToken cancellationToken)
    {
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                await action(cancellationToken).ConfigureAwait(false);
                return;
            }
            catch (Exception ex) when (attempt < MaxAttempts && IsTransient(ex, cancellationToken))
            {
                // 1s, 2s, 4s...
                var delay = TimeSpan.FromTicks(InitialRetryDelay.Ticks << (attempt - 1));
                await Task.Delay(delay, cancellationToken).ConfigureAwait(false);
            }
        }
    }

    // Network errors, interrupted streams, timeouts and transient HTTP status codes are retried.
    // Non-transient HTTP status codes and caller cancellations are not.
    private static bool IsTransient(Exception ex, CancellationToken cancellationToken)
    {
        return !cancellationToken.IsCancellationRequested && ex is not EphemeralMongoException;
    }
}
