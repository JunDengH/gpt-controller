namespace GptAccountManager.Infrastructure;

public static class AtomicFile
{
    public static async Task WriteAllBytesAsync(
        string path,
        ReadOnlyMemory<byte> content,
        CancellationToken cancellationToken = default)
    {
        var directory = Path.GetDirectoryName(path)
            ?? throw new InvalidOperationException("Target path has no directory.");
        Directory.CreateDirectory(directory);

        var tempPath = Path.Combine(
            directory,
            $".{Path.GetFileName(path)}.{Guid.NewGuid():N}.tmp");

        try
        {
            await using (var stream = new FileStream(
                tempPath,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.None,
                64 * 1024,
                FileOptions.Asynchronous | FileOptions.WriteThrough))
            {
                await stream.WriteAsync(content, cancellationToken);
                await stream.FlushAsync(cancellationToken);
                stream.Flush(flushToDisk: true);
            }

            Replace(tempPath, path);
        }
        finally
        {
            TryDelete(tempPath);
        }
    }

    public static Task WriteAllTextAsync(
        string path,
        string content,
        CancellationToken cancellationToken = default) =>
        WriteAllBytesAsync(path, System.Text.Encoding.UTF8.GetBytes(content), cancellationToken);

    private static void Replace(string tempPath, string targetPath)
    {
        if (!File.Exists(targetPath))
        {
            File.Move(tempPath, targetPath);
            return;
        }

        try
        {
            File.Replace(tempPath, targetPath, null, ignoreMetadataErrors: true);
        }
        catch (PlatformNotSupportedException)
        {
            File.Move(tempPath, targetPath, overwrite: true);
        }
        catch (IOException)
        {
            File.Move(tempPath, targetPath, overwrite: true);
        }
    }

    public static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch
        {
            // Best-effort cleanup. Startup cleanup handles leftovers.
        }
    }
}
