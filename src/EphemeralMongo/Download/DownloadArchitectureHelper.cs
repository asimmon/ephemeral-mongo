using System.Runtime.InteropServices;

namespace EphemeralMongo.Download;

internal static class DownloadArchitectureHelper
{
    public static string GetArchitecture()
    {
        return GetArchitecture(RuntimeInformation.ProcessArchitecture, RuntimeInformationHelper.OSPlatform);
    }

    public static string GetArchitecture(Architecture architecture, OSPlatform platform)
    {
        return architecture switch
        {
            Architecture.X86 or Architecture.X64 => "x86_64",

            // "arm64" seems to be returned only for macOS releases for both mongod and tools
            Architecture.Arm64 => platform == OSPlatform.OSX ? "arm64" : "aarch64",

            _ => throw new PlatformNotSupportedException($"Unsupported architecture {architecture}")
        };
    }
}