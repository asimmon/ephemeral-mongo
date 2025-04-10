using System.Diagnostics.CodeAnalysis;
using System.Text.Json;

namespace EphemeralMongo;

[SuppressMessage("Design", "CA1001:Types that own disposable fields should be disposable", Justification = "HttpClient is a singleton or managed by the caller")]
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

    internal async Task DownloadFileAsync(string url, string filePath, CancellationToken cancellationToken)
    {
        try
        {
            using var response = await this._httpClient.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
            response.EnsureSuccessStatusCode();

#if NET8_0_OR_GREATER
            Stream sourceStream;
            Stream destinationStream;
            await using ((sourceStream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false)).ConfigureAwait(false))
            await using ((destinationStream = File.Create(filePath)).ConfigureAwait(false))
            {
                await sourceStream.CopyToAsync(destinationStream, cancellationToken).ConfigureAwait(false);
            }
#else
            using var sourceStream = await response.Content.ReadAsStreamAsync().ConfigureAwait(false);
            using var destinationStream = File.Create(filePath);

            const int defaultBufferSize = 81920;
            await sourceStream.CopyToAsync(destinationStream, defaultBufferSize, cancellationToken).ConfigureAwait(false);
#endif
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException($"An error occurred while downloading the file from {url} to {filePath}", ex);
        }
    }

    internal async Task<TValue> GetFromJsonAsync<TValue>(string url, CancellationToken cancellationToken)
    {
        TValue? value;
        try
        {
            using var response = await this._httpClient.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
            response.EnsureSuccessStatusCode();

            Stream stream;
#if NET8_0_OR_GREATER
            await using ((stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false)).ConfigureAwait(false))
#elif NETSTANDARD2_1_OR_GREATER
            await using ((stream = await response.Content.ReadAsStreamAsync().ConfigureAwait(false)).ConfigureAwait(false))
#else
            using (stream = await response.Content.ReadAsStreamAsync().ConfigureAwait(false))
#endif
            {
                value = await JsonSerializer.DeserializeAsync<TValue>(stream, cancellationToken: cancellationToken).ConfigureAwait(false);
            }
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException($"An error occurred while parsing {url} as a JSON response", ex);
        }

        return value ?? throw new InvalidOperationException($"An error occurred while parsing {url} as a JSON response");
    }
}