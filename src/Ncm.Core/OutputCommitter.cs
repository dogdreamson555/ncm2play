using System.Collections.Concurrent;

namespace Ncm.Core;

public static class OutputCommitter
{
    private static readonly ConcurrentDictionary<string, object> DirectoryLocks = new(
        OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);

    public static OutputCommitResult Commit(
        string temporaryPath,
        OutputPlan plan,
        PlannedOutputItem item,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(temporaryPath);
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentNullException.ThrowIfNull(item);
        cancellationToken.ThrowIfCancellationRequested();

        var rootPath = OutputPathSecurity.NormalizeRoot(plan.RootPath);
        var targetDirectory = Path.GetFullPath(item.TargetDirectory);
        OutputPathSecurity.EnsureContained(rootPath, targetDirectory);
        if (plan.Options.Classification == OutputClassification.None)
        {
            if (!string.Equals(rootPath, targetDirectory, OutputPathSecurity.GetPathComparison()))
            {
                throw new IOException("未分类输出的目标目录必须是用户选择的输出目录。");
            }
        }
        else
        {
            OutputPathSecurity.EnsureSafeClassificationDirectory(rootPath, targetDirectory);
        }

        var extension = OutputPathSecurity.GetExtension(item.Source.OutputFormat);
        ValidateRequestedFileName(item.RequestedFileName, extension, targetDirectory);

        var fullTemporaryPath = Path.GetFullPath(temporaryPath);
        if (!File.Exists(fullTemporaryPath))
        {
            throw new FileNotFoundException("待提交的临时输出文件不存在。", fullTemporaryPath);
        }

        Directory.CreateDirectory(rootPath);
        if (!string.Equals(rootPath, targetDirectory, OutputPathSecurity.GetPathComparison()))
        {
            OutputPathSecurity.EnsureSafeClassificationDirectory(rootPath, targetDirectory);
            Directory.CreateDirectory(targetDirectory);
            OutputPathSecurity.EnsureSafeClassificationDirectory(rootPath, targetDirectory);
        }

        var directoryLock = DirectoryLocks.GetOrAdd(targetDirectory, static _ => new object());
        lock (directoryLock)
        {
            var collisionIndex = 1;
            while (true)
            {
                cancellationToken.ThrowIfCancellationRequested();
                OutputPathSecurity.EnsureSafeClassificationDirectory(rootPath, targetDirectory);
                var fileName = OutputPathSecurity.AddCollisionSuffix(item.RequestedFileName, collisionIndex);
                var targetPath = Path.GetFullPath(Path.Combine(targetDirectory, fileName));
                OutputPathSecurity.EnsureContained(rootPath, targetPath);
                EnsurePathLength(targetPath);

                if (ExistsCaseInsensitive(targetDirectory, fileName))
                {
                    collisionIndex = NextCollisionIndex(collisionIndex);
                    continue;
                }

                try
                {
                    File.Move(fullTemporaryPath, targetPath, overwrite: false);
                    return new OutputCommitResult(targetPath, collisionIndex);
                }
                catch (IOException) when (ExistsCaseInsensitive(targetDirectory, fileName))
                {
                    collisionIndex = NextCollisionIndex(collisionIndex);
                }
            }
        }
    }

    private static void ValidateRequestedFileName(string requestedFileName, string extension, string targetDirectory)
    {
        if (string.IsNullOrWhiteSpace(requestedFileName) ||
            !string.Equals(Path.GetFileName(requestedFileName), requestedFileName, StringComparison.Ordinal) ||
            requestedFileName.Contains(Path.DirectorySeparatorChar) ||
            requestedFileName.Contains(Path.AltDirectorySeparatorChar))
        {
            throw new IOException("规划文件名必须是单个安全的文件名。");
        }

        if (!string.Equals(Path.GetExtension(requestedFileName), extension, StringComparison.OrdinalIgnoreCase))
        {
            throw new IOException("规划文件扩展名与目标音频格式不一致。");
        }

        var stem = Path.GetFileNameWithoutExtension(requestedFileName);
        var sanitized = OutputPathSecurity.SanitizeFileName(stem, "未命名", extension, targetDirectory, out _);
        if (!string.Equals(sanitized, requestedFileName, StringComparison.Ordinal))
        {
            throw new IOException("规划文件名包含 Windows 不支持的字符或超出可用路径长度。");
        }
    }

    private static bool ExistsCaseInsensitive(string directory, string fileName)
    {
        if (!Directory.Exists(directory))
        {
            return false;
        }

        return Directory.EnumerateFileSystemEntries(directory)
            .Any(entry => string.Equals(Path.GetFileName(entry), fileName, StringComparison.OrdinalIgnoreCase));
    }

    private static int NextCollisionIndex(int current)
    {
        if (current == int.MaxValue)
        {
            throw new IOException("目标目录中同名文件过多，无法分配新的文件名。");
        }

        return current + 1;
    }

    private static void EnsurePathLength(string targetPath)
    {
        if (OperatingSystem.IsWindows() && targetPath.Length > 259)
        {
            throw new PathTooLongException($"输出路径超过 Windows 当前配置可用的长度：{targetPath}");
        }
    }
}
