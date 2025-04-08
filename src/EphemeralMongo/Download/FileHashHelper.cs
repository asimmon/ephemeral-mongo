using System.Security.Cryptography;

namespace EphemeralMongo.Download;

internal static class FileHashHelper
{
    public static Task EnsureFileSha256HashAsync(string filePath, string expectedHash, CancellationToken cancellationToken)
    {
        string hash;

        try
        {
            using var hasher = SHA256.Create();
            using var fileStream = File.OpenRead(filePath);
            var hashBytes = hasher.ComputeHash(fileStream);
            hash = BitConverter.ToString(hashBytes).Replace("-", string.Empty);
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException($"Failed to compute SHA256 hash for file {filePath}", ex);
        }

        if (!hash.Equals(expectedHash, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException($"The hash of the downloaded file {filePath} does not match the expected hash. Expected: {expectedHash}, Actual: {hash}");
        }

        return Task.CompletedTask;
    }
}