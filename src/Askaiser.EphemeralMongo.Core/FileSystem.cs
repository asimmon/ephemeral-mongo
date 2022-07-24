using System.Diagnostics;
using System.Runtime.InteropServices;

namespace Askaiser.EphemeralMongo.Core;

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
            var chmod = Process.Start("chmod", "+x " + path);
            if (chmod != null)
            {
                try
                {
                    chmod.WaitForExit();

                    if (chmod.ExitCode != 0)
                    {
                        throw new IOException($"Could not set executable bit for '{path}'");
                    }
                }
                finally
                {
                    chmod.Dispose();
                }
            }
        }
    }
}