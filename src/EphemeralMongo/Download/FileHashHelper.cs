using System.Security.Cryptography;

namespace EphemeralMongo.Download;

internal static class FileHashHelper
{
#if NETSTANDARD2_0
    public static Task EnsureFileSha256HashAsync(string filePath, string expectedHash, CancellationToken cancellationToken)
#else
    public static async Task EnsureFileSha256HashAsync(string filePath, string expectedHash, CancellationToken cancellationToken)
#endif
    {
        string hash;

        try
        {
            Stream fileStream;
#if NETSTANDARD2_0
            using (fileStream = File.OpenRead(filePath))
#else
            await using ((fileStream = File.OpenRead(filePath)).ConfigureAwait(false))
#endif
            {
                using var hasher = SHA256.Create();
#if NET8_0_OR_GREATER
                var hashBytes = await hasher.ComputeHashAsync(fileStream, cancellationToken).ConfigureAwait(false);
                hash = Convert.ToHexString(hashBytes);
#else
                var hashBytes = hasher.ComputeHash(fileStream);
                hash = BitConverter.ToString(hashBytes).Replace("-", string.Empty);
#endif
            }
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException($"Failed to compute SHA256 hash for file {filePath}", ex);
        }

        if (!hash.Equals(expectedHash, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException($"The hash of the downloaded file {filePath} does not match the expected hash. Expected: {expectedHash}, Actual: {hash}");
        }

#if NETSTANDARD2_0
        return Task.CompletedTask;
#endif
    }
}