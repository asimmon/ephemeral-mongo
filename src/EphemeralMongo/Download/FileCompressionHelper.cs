using System.Diagnostics;
using System.IO.Compression;

namespace EphemeralMongo.Download;

internal static class FileCompressionHelper
{
    public static async Task ExtractToDirectoryAsync(string archiveFilePath, string destinationDirectoryPath, CancellationToken cancellationToken)
    {
        if (archiveFilePath.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
        {
            await ExtractZipToDirectoryAsync(archiveFilePath, destinationDirectoryPath, cancellationToken).ConfigureAwait(false);
        }
        else if (archiveFilePath.EndsWith(".tgz", StringComparison.OrdinalIgnoreCase))
        {
            await ExtractTarGzToDirectoryAsync(archiveFilePath, destinationDirectoryPath, cancellationToken).ConfigureAwait(false);
        }
        else
        {
            throw new NotSupportedException($"The archive file format is not supported: {archiveFilePath}");
        }
    }

    private static Task ExtractZipToDirectoryAsync(string archiveFilePath, string destinationDirectory, CancellationToken cancellationToken)
    {
        try
        {
            ZipFile.ExtractToDirectory(archiveFilePath, destinationDirectory);
            return Task.CompletedTask;
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException($"Failed to extract zip file {archiveFilePath} to {destinationDirectory}", ex);
        }
    }

    private static async Task ExtractTarGzToDirectoryAsync(string archiveFilePath, string destinationDirectory, CancellationToken cancellationToken)
    {
        var processStartInfo = new ProcessStartInfo
        {
            FileName = "tar",
            Arguments = $"-xzf {ProcessArgument.Escape(archiveFilePath)} -C {ProcessArgument.Escape(destinationDirectory)}",
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };

        if (Process.Start(processStartInfo) is not { } process)
        {
            throw new InvalidOperationException($"Failed to start the tar process to extract {archiveFilePath} to {destinationDirectory}");
        }

        try
        {
            using (process)
            {
                process.EnableRaisingEvents = true;

                var tcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);

                void OnProcessExited(object? sender, EventArgs args)
                {
                    var exitCode = process.ExitCode;
                    if (exitCode == 0)
                    {
                        tcs.SetResult(true);
                    }
                    else
                    {
                        tcs.SetException(new InvalidOperationException($"The tar process exited with code {exitCode}"));
                    }
                }

                process.Exited += OnProcessExited;

                try
                {
                    using (cancellationToken.Register(() => tcs.TrySetCanceled(cancellationToken)))
                    {
                        await tcs.Task.ConfigureAwait(false);
                    }
                }
                finally
                {
                    process.Exited -= OnProcessExited;
                    process.Kill();
#if NET8_0_OR_GREATER
                    await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
#else
                    process.WaitForExit();
#endif
                }
            }
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException($"Failed to extract tar.gz file {archiveFilePath} to {destinationDirectory}", ex);
        }
    }
}