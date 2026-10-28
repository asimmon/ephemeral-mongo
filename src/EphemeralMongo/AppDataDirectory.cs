#if !NET8_0_OR_GREATER
using System.Diagnostics;
#endif
using System.Runtime.InteropServices;

namespace EphemeralMongo;

// Downloaded binaries, temporary files and default data directories all live in the per-user local app data directory.
// The system temp directory can be shared by all users (/tmp on Linux, /tmp on macOS when TMPDIR isn't set, C:\Windows\Temp for SYSTEM on .NET Framework),
// so another user could pre-create our directories and tamper with the archives, the extracted executables or the data files.
internal static class AppDataDirectory
{
    public static readonly string RootPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ephemeral-mongo");

    public static readonly string TmpPath = Path.Combine(RootPath, "tmp");

    public static readonly string DataPath = Path.Combine(RootPath, "data");

    private static volatile bool _isRootRestricted;

    public static void EnsureRootExists()
    {
        if (_isRootRestricted && Directory.Exists(RootPath))
        {
            return;
        }

        Directory.CreateDirectory(RootPath);

        // The root directory might have been created with a permissive umask or by an older version of this library.
        // Restricting the root is enough, as other users can't traverse it to reach anything below.
        RestrictToOwner(RootPath);

        _isRootRestricted = true;
    }

    private static void RestrictToOwner(string path)
    {
        // On Windows, the local app data directory is already only accessible to its owner
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Linux) && !RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
        {
            return;
        }

        try
        {
#if NET8_0_OR_GREATER
            const UnixFileMode ownerOnlyPermissions = UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute;

            if (File.GetUnixFileMode(path) != ownerOnlyPermissions)
            {
                File.SetUnixFileMode(path, ownerOnlyPermissions);
            }
#else
            using var chmod = Process.Start("chmod", "700 " + ProcessArgument.Escape(path));
            chmod?.WaitForExit();
#endif
        }
        catch
        {
            // Do not throw if something wrong happens, such as the directory being owned by another user who already restricted it.
            // If we can't write to it, we'll see it later.
        }
    }
}
