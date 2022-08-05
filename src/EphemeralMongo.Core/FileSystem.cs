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

    public void MakeFileExecutable(string path)
    {
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux) || RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
        {
            // Do not throw if exit code is not equal to zero.
            // If there's something wrong with the path or permissions, we'll see it later.
            using var chmod = Process.Start("chmod", "+x " + ProcessArgument.Escape(path));
            chmod?.WaitForExit();
        }
    }
}