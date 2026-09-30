namespace Ncm.Core;

public static class NcmQueueScanner
{
    private static readonly StringComparer PathComparer = StringComparer.OrdinalIgnoreCase;

    public static Task<NcmQueueScanResult> ScanAsync(
        IEnumerable<string> paths,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(paths);
        return Task.Run(() => Scan(paths, cancellationToken), CancellationToken.None);
    }

    private static NcmQueueScanResult Scan(IEnumerable<string> paths, CancellationToken cancellationToken)
    {
        var files = new HashSet<string>(PathComparer);
        var errors = new List<NcmQueueScanError>();

        foreach (var path in paths)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (string.IsNullOrWhiteSpace(path))
            {
                errors.Add(new NcmQueueScanError(path ?? string.Empty, "路径为空。"));
                continue;
            }

            string fullPath;
            try
            {
                fullPath = Path.GetFullPath(path);
            }
            catch (Exception exception) when (IsPathException(exception))
            {
                errors.Add(new NcmQueueScanError(path, $"无法规范化路径：{exception.Message}"));
                continue;
            }

            try
            {
                var attributes = File.GetAttributes(fullPath);
                if ((attributes & FileAttributes.Directory) == 0)
                {
                    AddNcmFile(fullPath, files);
                    continue;
                }

                if ((attributes & FileAttributes.ReparsePoint) != 0)
                {
                    errors.Add(new NcmQueueScanError(fullPath, "拒绝扫描目录链接或联接点。"));
                    continue;
                }

                ScanDirectory(fullPath, files, errors, cancellationToken);
            }
            catch (Exception exception) when (IsPathException(exception))
            {
                errors.Add(new NcmQueueScanError(fullPath, $"无法访问路径：{exception.Message}"));
            }
        }

        return new NcmQueueScanResult(
            files.OrderBy(path => path, PathComparer).ThenBy(path => path, StringComparer.Ordinal).ToArray(),
            errors.ToArray());
    }

    private static void ScanDirectory(
        string rootPath,
        HashSet<string> files,
        List<NcmQueueScanError> errors,
        CancellationToken cancellationToken)
    {
        var pending = new Stack<string>();
        pending.Push(rootPath);

        while (pending.TryPop(out var directoryPath))
        {
            cancellationToken.ThrowIfCancellationRequested();

            try
            {
                var attributes = File.GetAttributes(directoryPath);
                if ((attributes & FileAttributes.ReparsePoint) != 0)
                {
                    errors.Add(new NcmQueueScanError(directoryPath, "已跳过目录链接或联接点。"));
                    continue;
                }

                foreach (var entryPath in Directory.EnumerateFileSystemEntries(directoryPath))
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    try
                    {
                        var entryAttributes = File.GetAttributes(entryPath);
                        if ((entryAttributes & FileAttributes.Directory) != 0)
                        {
                            if ((entryAttributes & FileAttributes.ReparsePoint) != 0)
                            {
                                errors.Add(new NcmQueueScanError(entryPath, "已跳过目录链接或联接点。"));
                            }
                            else
                            {
                                pending.Push(entryPath);
                            }

                            continue;
                        }

                        AddNcmFile(entryPath, files);
                    }
                    catch (Exception exception) when (IsPathException(exception))
                    {
                        errors.Add(new NcmQueueScanError(entryPath, $"无法检查路径：{exception.Message}"));
                    }
                }
            }
            catch (Exception exception) when (IsPathException(exception))
            {
                errors.Add(new NcmQueueScanError(directoryPath, $"无法读取目录：{exception.Message}"));
            }
        }
    }

    private static void AddNcmFile(string path, HashSet<string> files)
    {
        if (Path.GetExtension(path).Equals(".ncm", StringComparison.OrdinalIgnoreCase))
        {
            files.Add(Path.GetFullPath(path));
        }
    }

    private static bool IsPathException(Exception exception) =>
        exception is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException or
        System.Security.SecurityException;
}
