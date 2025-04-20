#if !NET8_0_OR_GREATER
using System.Diagnostics;
#endif
using System.Runtime.InteropServices;

namespace EphemeralMongo;

internal sealed class FileSystem : IFileSystem
{
    public void CreateDirectory(string path)
    {
        Directory.CreateDirectory(path);
    }

    public void DeleteDirectory(string path)
    {
        Directory.Delete(path, recursive: true);
    }

    public void DeleteFile(string path)
    {
        File.Delete(path);
    }

    public string[] GetDirectories(string path, string searchPattern, SearchOption searchOption)
    {
        return Directory.GetDirectories(path, searchPattern, searchOption);
    }

    public DateTime GetDirectoryCreationTimeUtc(string path)
    {
        return Directory.GetCreationTimeUtc(path);
    }

    public void MakeFileExecutable(string path)
    {
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Linux) && !RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
        {
            return;
        }

        try
        {
#if NET8_0_OR_GREATER
            // const UnixFileMode executePermissions = UnixFileMode.UserExecute | UnixFileMode.GroupExecute | UnixFileMode.OtherExecute;

            // var unixFileMode = File.GetUnixFileMode(path);
            // var alreadyExecutable = (unixFileMode & executePermissions) != UnixFileMode.None;

            // if (!alreadyExecutable)
            // {
            //     File.SetUnixFileMode(path, unixFileMode | executePermissions);
            // }
#else
            using var test = Process.Start("test", "-x " + ProcessArgument.Escape(path));
            test?.WaitForExit();

            var alreadyExecutable = test?.ExitCode == 0;
            if (alreadyExecutable)
            {
                return;
            }

            using var chmod = Process.Start("chmod", "+x " + ProcessArgument.Escape(path));
            chmod?.WaitForExit();
#endif
        }
        catch
        {
            // Do not throw if something wrong happens
            // If there's something wrong with the path or permissions, we'll see it later.
        }
    }
}