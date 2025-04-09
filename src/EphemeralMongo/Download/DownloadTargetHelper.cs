using System.Runtime.InteropServices;
using System.Text.RegularExpressions;

namespace EphemeralMongo.Download;

internal sealed class DownloadTargetHelper(string linuxOsReleasePath)
{
    private static readonly DownloadTargetHelper Instance = new DownloadTargetHelper("/etc/os-release");

    public static string GetTarget(MongoVersion version)
    {
        return Instance.GetTarget(RuntimeInformationHelper.OSPlatform, version);
    }

    public string GetTarget(OSPlatform platform, MongoVersion version)
    {
        if (platform == OSPlatform.Windows)
        {
            return "windows";
        }

        if (platform == OSPlatform.OSX)
        {
            return "macos";
        }

        if (platform == OSPlatform.Linux)
        {
            return this.GetAdjustedLinuxTarget(version);
        }

        throw new PlatformNotSupportedException($"Unsupported operating system {RuntimeInformation.OSDescription}");
    }

    private string GetAdjustedLinuxTarget(MongoVersion version)
    {
        // Some Linux distributions have limited support for older MongoDB versions.
        // For instance, there's no specific MongoDB binaries targeting Ubuntu 24.04 for MongoDB 7 and earlier,
        // but it works if use the Ubuntu 22.04 binaries on Ubuntu 24.04.
        // We're focusing on Ubuntu for now as it's the most common Linux distribution for CIs.
        var originalTarget = this.GetLinuxTarget();

        if (originalTarget == "ubuntu2404" && version is MongoVersion.V6 or MongoVersion.V7)
        {
            return "ubuntu2204";
        }

        return originalTarget;
    }

    // Matches ID and VERSION_ID keys and their values without quotes in /etc/os-release
    private static readonly Regex LinuxOsReleaseKeyValueRegex = new Regex(
        "^(?<key>ID|VERSION_ID)=\"?(?<value>[^\"]+)\"?$",
        RegexOptions.IgnoreCase | RegexOptions.Singleline);

    private string GetLinuxTarget()
    {
        string? target = null;
        try
        {
            string? id = null;
            string? versionId = null;

            foreach (var line in File.ReadAllLines(linuxOsReleasePath))
            {
                if (LinuxOsReleaseKeyValueRegex.Match(line) is { Success: true } match)
                {
                    var key = match.Groups["key"].Value;
                    var value = match.Groups["value"].Value;

                    if (key.Equals("ID", StringComparison.OrdinalIgnoreCase))
                    {
                        id = value;
                    }
                    else if (key.Equals("VERSION_ID", StringComparison.OrdinalIgnoreCase))
                    {
                        versionId = value;
                    }

                    if (id != null && versionId != null)
                    {
                        target = GetLinuxTargetFromOsRelease(id, versionId);
                    }
                }
            }
        }
        catch (IOException)
        {
            // We'll use the fallback target
        }

        const string fallbackTarget = "ubuntu2204";
        return target ?? fallbackTarget;
    }

    // Written using https://downloads.mongodb.org/current.json from 2025-04-05
    // Backup available here: https://gist.github.com/asimmon/60e49484832e985a1d51a672e2d3e028
    // Used Docker to access the possible values in /etc/os-release
    // Ex: docker run --rm ubuntu:24.04 cat /etc/os-release
    private static string? GetLinuxTargetFromOsRelease(string id, string versionId)
    {
        if ("ubuntu".Equals(id, StringComparison.OrdinalIgnoreCase))
        {
            return GetUbuntuTarget(versionId);
        }

        if ("debian".Equals(id, StringComparison.OrdinalIgnoreCase))
        {
            return GetDebianTarget(versionId);
        }

        // ID like opensuse-leap or opensuse-tumbleweed (https://hub.docker.com/_/opensuse)
        if (id.StartsWith("opensuse", StringComparison.OrdinalIgnoreCase))
        {
            return GetOpenSuseTarget(versionId);
        }

        if ("rhel".Equals(id, StringComparison.OrdinalIgnoreCase))
        {
            return GetRedHatEnterpriseLinuxTarget(versionId);
        }

        // Amazon Linux (https://hub.docker.com/_/amazonlinux)
        if ("amzn".Equals(id, StringComparison.OrdinalIgnoreCase))
        {
            return GetAmazonLinuxTarget(versionId);
        }

        return null;
    }

    private static string GetUbuntuTarget(string versionId)
    {
        string? target = null;

        if (Version.TryParse(EnsureAtLeastTwoVersionParts(versionId), out var version))
        {
            target = version.Major switch
            {
                >= 24 => "ubuntu2404",
                >= 22 => "ubuntu2204",
                >= 20 => "ubuntu2004",
                >= 18 => "ubuntu1804",
                _ => null
            };
        }

        return target ?? throw new NotSupportedException($"Unsupported Ubuntu version {versionId}");
    }

    private static string GetDebianTarget(string versionId)
    {
        string? target = null;

        if (Version.TryParse(EnsureAtLeastTwoVersionParts(versionId), out var version))
        {
            target = version.Major switch
            {
                >= 12 => "debian12",
                >= 11 => "debian11",
                >= 10 => "debian10",
                _ => null
            };
        }

        return target ?? throw new NotSupportedException($"Unsupported Debian version {versionId}");
    }

    private static string GetOpenSuseTarget(string versionId)
    {
        string? target = null;
        if (Version.TryParse(EnsureAtLeastTwoVersionParts(versionId), out var version))
        {
            target = version.Major switch
            {
                >= 15 => "suse15",
                >= 12 => "suse12",
                _ => null
            };
        }

        return target ?? throw new NotSupportedException($"Unsupported OpenSUSE version {versionId}");
    }

    private static string GetRedHatEnterpriseLinuxTarget(string versionId)
    {
        if (Version.TryParse(EnsureAtLeastTwoVersionParts(versionId), out var version))
        {
            if (version >= new Version(9, 3))
            {
                return "rhel93";
            }

            if (version >= new Version(9, 0))
            {
                return "rhel90";
            }

            if (version >= new Version(8, 3))
            {
                return "rhel83";
            }

            if (version >= new Version(8, 1))
            {
                return "rhel81";
            }

            if (version >= new Version(8, 0))
            {
                return "rhel8";
            }

            if (version >= new Version(7, 2))
            {
                return "rhel72";
            }

            if (version >= new Version(7, 0))
            {
                return "rhel70";
            }
        }

        throw new NotSupportedException($"Unsupported Red Hat Enterprise Linux version {version}");
    }

    private static string GetAmazonLinuxTarget(string versionId) => versionId switch
    {
        "2023" => "amazon2023",
        "2" => "amazon2",
        _ => throw new NotSupportedException($"Unsupported Amazon Linux version {versionId}")
    };

    private static string EnsureAtLeastTwoVersionParts(string version)
    {
        // System.Version requires at least two parts (e.g., 18.04)
#if  NETSTANDARD2_0
        return version.IndexOf(".", StringComparison.Ordinal) == -1 ? $"{version}.0" : version;
#else
        return version.Contains('.', StringComparison.Ordinal) ? version : $"{version}.0";
#endif
    }
}