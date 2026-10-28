using System.Text.RegularExpressions;

namespace EphemeralMongo.Download;

// The release information documents are the only source of truth for what gets downloaded, written to disk and executed.
// Archive hashes come from the same documents as their URLs, so these checks limit what a tampered document could achieve.
internal static class DownloadReleaseHelper
{
    private static readonly Regex ArchiveFileNameRegex = new Regex(
        @"^[a-z0-9][a-z0-9._-]*\.(?:zip|tgz)$",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    // Versions are used as directory names, so they must not be able to escape the base directory
    public static string GetVersionDirectoryName(string version)
    {
        if (Version.TryParse(version, out var parsedVersion))
        {
            // Normalized the same way as existing directories are looked up
            return parsedVersion.ToString();
        }

        throw new EphemeralMongoException($"The MongoDB release version '{version}' is not a valid version");
    }

    // Archives are only downloaded over HTTPS from MongoDB servers, and their file names must not be able to escape the download directory
    public static string GetArchiveFileName(string url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps || !IsMongoDbHost(uri.Host))
        {
            throw new EphemeralMongoException($"The MongoDB archive URL '{url}' is not an HTTPS URL from mongodb.org or mongodb.com");
        }

        // Path.GetFileName isn't used as its separators depend on the operating system.
        // Only plain file names are allowed, as characters such as ':' could turn them into drive-relative paths or alternate data streams on Windows.
        var fileName = uri.AbsolutePath.Substring(uri.AbsolutePath.LastIndexOf('/') + 1);
        if (!ArchiveFileNameRegex.IsMatch(fileName))
        {
            throw new EphemeralMongoException($"The MongoDB archive URL '{url}' does not point to a .zip or .tgz file");
        }

        return fileName;
    }

    // Community archives are served from fastdl.mongodb.org, enterprise ones from downloads.mongodb.com
    private static bool IsMongoDbHost(string host)
    {
        return IsDomainOrSubdomain(host, "mongodb.org") || IsDomainOrSubdomain(host, "mongodb.com");
    }

    private static bool IsDomainOrSubdomain(string host, string domain)
    {
        return host.Equals(domain, StringComparison.OrdinalIgnoreCase) || host.EndsWith("." + domain, StringComparison.OrdinalIgnoreCase);
    }
}
