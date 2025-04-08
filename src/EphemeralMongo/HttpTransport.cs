using System.Diagnostics.CodeAnalysis;
using System.Text.Json;

namespace EphemeralMongo;

[SuppressMessage("Design", "CA1001:Types that own disposable fields should be disposable", Justification = "HttpClient is a singleton or managed by the caller")]
public sealed class HttpTransport
{
    // TODO use socket http handler for when targeting .NET Core
    private static readonly HttpClient SharedDefaultHttpClient = new HttpClient();

    private readonly HttpClient _httpClient;

    public HttpTransport(HttpClient httpClient)
    {
        this._httpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
    }

    public HttpTransport()
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

            using var sourceStream = await response.Content.ReadAsStreamAsync().ConfigureAwait(false);
            using var destinationStream = File.Create(filePath);

            const int defaultBufferSize = 81920;
            await sourceStream.CopyToAsync(destinationStream, defaultBufferSize, cancellationToken).ConfigureAwait(false);
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

            using var stream = await response.Content.ReadAsStreamAsync().ConfigureAwait(false);
            value = await JsonSerializer.DeserializeAsync<TValue>(stream, cancellationToken: cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException($"An error occurred while parsing {url} as a JSON response", ex);
        }

        return value ?? throw new InvalidOperationException($"An error occurred while parsing {url} as a JSON response");
    }
}