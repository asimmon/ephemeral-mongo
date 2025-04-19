using System.Diagnostics;
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
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux) || RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
        {
#if NET8_0_OR_GREATER
            try
            {
                var unixMode = File.GetUnixFileMode(path);
                var alreadyExecutable = unixMode.HasFlag(UnixFileMode.UserExecute) || unixMode.HasFlag(UnixFileMode.GroupExecute) || unixMode.HasFlag(UnixFileMode.OtherExecute);
                if (alreadyExecutable)
                {
                    return;
                }
            }
            catch
            {
                // If test command fails (not found, etc.), fall through to chmod
            }
#else
            try
            {
                using var testProcess = Process.Start("test", "-x " + ProcessArgument.Escape(path));
                testProcess?.WaitForExit();

                var alreadyExecutable = testProcess?.ExitCode == 0;
                if (alreadyExecutable)
                {
                    return;
                }
            }
            catch
            {
                // If test command fails (not found, etc.), fall through to chmod
            }
#endif

            // Do not throw if exit code is not equal to zero.
            // If there's something wrong with the path or permissions, we'll see it later.
            using var chmod = Process.Start("chmod", "+x " + ProcessArgument.Escape(path));
            chmod?.WaitForExit();
        }
    }
}