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
            Architecture.Arm64 when platform == OSPlatform.OSX => "arm64",

            // As of 2026-10-28, there are no Windows arm64 releases for mongod and tools, but Windows 11 on ARM can run x64 binaries through emulation
            Architecture.Arm64 when platform == OSPlatform.Windows => "x86_64",

            Architecture.Arm64 => "aarch64",

            _ => throw new PlatformNotSupportedException($"Unsupported architecture {architecture}")
        };
    }
}