using System.Diagnostics.CodeAnalysis;
using System.Runtime.InteropServices;

namespace EphemeralMongo.Download;

[SuppressMessage("ReSharper", "InconsistentNaming", Justification = "Same casing as System.Runtime.InteropServices.OSPlatform")]
internal static class RuntimeInformationHelper
{
    private static readonly Lazy<OSPlatform> LazyOSPlatform = new Lazy<OSPlatform>(GetOSPlatform);

    public static OSPlatform OSPlatform => LazyOSPlatform.Value;

    private static OSPlatform GetOSPlatform()
    {
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            return OSPlatform.Windows;
        }

        if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
        {
            return OSPlatform.OSX;
        }

        if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
        {
            return OSPlatform.Linux;
        }

        throw new PlatformNotSupportedException($"Unsupported operating system {RuntimeInformation.OSDescription}");
    }
}