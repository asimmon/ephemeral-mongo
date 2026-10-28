using System.Diagnostics;
using System.IO.Compression;
using System.Text;

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
            throw new EphemeralMongoException($"Failed to extract zip file {archiveFilePath} to {destinationDirectory}", ex);
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

        var output = new StringBuilder();

        void OnDataReceived(object sender, DataReceivedEventArgs args)
        {
            if (args.Data != null)
            {
                // Standard output and error events are raised on different threads
                lock (output)
                {
                    output.AppendLine(args.Data);
                }
            }
        }

        var processExitedTcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);

        // Subscribing to the exit event before starting the process ensures that we can't miss it if tar exits immediately
        using var process = new Process
        {
            StartInfo = processStartInfo,
            EnableRaisingEvents = true,
        };

        process.OutputDataReceived += OnDataReceived;
        process.ErrorDataReceived += OnDataReceived;
        process.Exited += (_, _) => processExitedTcs.TrySetResult(true);

        try
        {
            process.Start();

            // Both streams must be drained, otherwise tar blocks once the pipe buffer is full
            process.BeginOutputReadLine();
            process.BeginErrorReadLine();

            try
            {
                using (cancellationToken.Register(() => processExitedTcs.TrySetCanceled(cancellationToken)))
                {
                    await processExitedTcs.Task.ConfigureAwait(false);
                }
            }
            catch (OperationCanceledException)
            {
                KillQuietly(process);
                throw;
            }

            // Ensures that all redirected output has been received
#if NET8_0_OR_GREATER
            await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
#else
            process.WaitForExit();
#endif

            if (process.ExitCode != 0)
            {
                string outputMessage;
                lock (output)
                {
                    outputMessage = output.Length == 0 ? string.Empty : $" Output: {output}";
                }

                throw new InvalidOperationException($"The tar process exited with code {process.ExitCode}.{outputMessage}");
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            KillQuietly(process);
            throw new EphemeralMongoException($"Failed to extract tar.gz file {archiveFilePath} to {destinationDirectory}", ex);
        }
    }

    private static void KillQuietly(Process process)
    {
        try
        {
            if (!process.HasExited)
            {
                process.Kill();
                process.WaitForExit();
            }
        }
        catch
        {
            // Ignored, the process either never started or already exited
        }
    }
}
