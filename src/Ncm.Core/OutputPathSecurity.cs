using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace Ncm.Core;

internal static class OutputPathSecurity
{
    private const int WindowsMaxComponentLength = 255;
    private const int WindowsMaxPathLength = 259;
    private const int CollisionSuffixReserve = 24;
    private static readonly HashSet<string> ReservedNames = CreateReservedNames();

    public static string NormalizeRoot(string outputDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(outputDirectory);
        return TrimEndingSeparators(Path.GetFullPath(outputDirectory));
    }

    public static string GetTargetDirectory(
        string rootPath,
        OutputClassification classification,
        string groupName,
        out bool nameWasShortened)
    {
        nameWasShortened = false;
        if (classification == OutputClassification.None)
        {
            return rootPath;
        }

        var minimumLeafLength = 1 + GetExtension(NcmAudioFormat.Flac).Length;
        var separatorLength = Path.DirectorySeparatorChar.ToString().Length;
        var availableGroupLength = GetMaximumPathLength() - rootPath.Length - separatorLength * 2 - minimumLeafLength - CollisionSuffixReserve;
        if (OperatingSystem.IsWindows())
        {
            availableGroupLength = Math.Min(availableGroupLength, WindowsMaxComponentLength);
        }

        if (availableGroupLength < 1)
        {
            throw new PathTooLongException($"输出目录过长，无法在其中创建分类目录：{rootPath}");
        }

        var safeGroup = SanitizeComponent(groupName, "未分类", availableGroupLength, out nameWasShortened);
        var candidate = Path.GetFullPath(Path.Combine(rootPath, safeGroup));
        EnsureContained(rootPath, candidate);
        return FindExistingCaseInsensitiveEntry(rootPath, safeGroup, directoryOnly: true) ?? candidate;
    }

    public static string SanitizeFileName(string rawName, string fallback, string extension, string targetDirectory, out bool shortened)
    {
        var separatorsLength = Path.DirectorySeparatorChar.ToString().Length;
        var maxPathLength = GetMaximumPathLength();
        var maxComponentLength = OperatingSystem.IsWindows() ? WindowsMaxComponentLength : 255;
        var availableByPath = maxPathLength - targetDirectory.Length - separatorsLength - CollisionSuffixReserve;
        var maximumLength = Math.Min(maxComponentLength, availableByPath);
        if (maximumLength <= extension.Length + 1 + 8)
        {
            throw new PathTooLongException($"输出路径过长，无法为文件名和重名后缀留出空间：{targetDirectory}");
        }

        var requested = SanitizeComponent(rawName, fallback, maximumLength - extension.Length, out shortened);
        if (requested.Length == 0)
        {
            requested = SanitizeComponent(fallback, "未命名", maximumLength - extension.Length, out shortened);
        }

        var fileName = requested + extension;
        if (fileName.Length > maximumLength)
        {
            fileName = TruncateWithHash(requested + extension, maximumLength, out _);
            shortened = true;
        }

        var target = Path.GetFullPath(Path.Combine(targetDirectory, fileName));
        if (OperatingSystem.IsWindows() && target.Length > WindowsMaxPathLength)
        {
            throw new PathTooLongException($"输出路径超过 Windows 当前配置可用的长度：{target}");
        }

        return fileName;
    }

    public static string SanitizeComponent(string value, string fallback, int maximumLength, out bool shortened)
    {
        ArgumentNullException.ThrowIfNull(value);
        if (maximumLength < 1)
        {
            throw new PathTooLongException("输出路径过长，无法容纳有效文件名。");
        }

        var builder = new StringBuilder(value.Length);
        foreach (var character in value)
        {
            builder.Append(IsInvalidWindowsCharacter(character) ? '_' : character);
        }

        var sanitized = TrimWindowsEnding(builder.ToString());
        if (sanitized.Length == 0)
        {
            sanitized = TrimWindowsEnding(fallback);
        }

        if (IsReservedDeviceName(sanitized))
        {
            sanitized = "_" + sanitized;
        }

        shortened = sanitized.Length > maximumLength;
        if (shortened)
        {
            sanitized = TruncateWithHash(sanitized, maximumLength, out _);
        }

        sanitized = TrimWindowsEnding(sanitized);
        if (IsReservedDeviceName(sanitized))
        {
            sanitized = sanitized.Length < maximumLength ? "_" + sanitized : "_";
        }

        if (sanitized.Length == 0)
        {
            sanitized = "_";
        }

        return sanitized;
    }

    public static string AddCollisionSuffix(string fileName, int collisionIndex)
    {
        if (collisionIndex < 2)
        {
            return fileName;
        }

        var extension = Path.GetExtension(fileName);
        var stem = fileName[..^extension.Length];
        return $"{stem} ({collisionIndex}){extension}";
    }

    public static string GetExtension(NcmAudioFormat format) => format switch
    {
        NcmAudioFormat.Mp3 => ".mp3",
        NcmAudioFormat.Flac => ".flac",
        _ => throw new ArgumentOutOfRangeException(nameof(format), format, "不支持的输出音频格式。")
    };

    public static void EnsureContained(string rootPath, string candidatePath)
    {
        var relative = Path.GetRelativePath(rootPath, candidatePath);
        if (Path.IsPathRooted(relative) || relative == ".." ||
            relative.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal) ||
            relative.StartsWith(".." + Path.AltDirectorySeparatorChar, StringComparison.Ordinal))
        {
            throw new IOException($"目标路径超出输出目录：{candidatePath}");
        }
    }

    public static bool ExistsCaseInsensitive(string directory, string name)
    {
        if (!Directory.Exists(directory))
        {
            return false;
        }

        return Directory.EnumerateFileSystemEntries(directory)
            .Any(entry => string.Equals(Path.GetFileName(entry), name, StringComparison.OrdinalIgnoreCase));
    }

    public static void EnsurePathLength(string targetPath)
    {
        if (OperatingSystem.IsWindows() && targetPath.Length > WindowsMaxPathLength)
        {
            throw new PathTooLongException($"输出路径超过 Windows 当前配置可用的长度：{targetPath}");
        }
    }

    public static string? FindExistingCaseInsensitiveEntry(string parentDirectory, string name, bool directoryOnly)
    {
        if (!Directory.Exists(parentDirectory))
        {
            return null;
        }

        foreach (var entry in Directory.EnumerateFileSystemEntries(parentDirectory))
        {
            if (!string.Equals(Path.GetFileName(entry), name, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var attributes = File.GetAttributes(entry);
            var isDirectory = (attributes & FileAttributes.Directory) != 0;
            if (directoryOnly && !isDirectory)
            {
                throw new IOException($"分类目录名称已被文件占用：{entry}");
            }

            return entry;
        }

        return null;
    }

    public static void EnsureSafeClassificationDirectory(string rootPath, string targetDirectory)
    {
        EnsureContained(rootPath, targetDirectory);
        if (string.Equals(rootPath, targetDirectory, GetPathComparison()))
        {
            return;
        }

        var relative = Path.GetRelativePath(rootPath, targetDirectory);
        if (Path.IsPathRooted(relative) || relative.Length == 0 ||
            relative.IndexOfAny([Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar]) >= 0 ||
            relative is "." or "..")
        {
            throw new IOException($"分类目录必须是输出目录的直接子目录：{targetDirectory}");
        }

        var existing = FindExistingCaseInsensitiveEntry(rootPath, relative, directoryOnly: true);
        if (existing is null)
        {
            return;
        }

        if ((File.GetAttributes(existing) & FileAttributes.ReparsePoint) != 0)
        {
            throw new IOException($"分类目录不能是符号链接或联接点：{existing}");
        }
    }

    public static StringComparison GetPathComparison() => OperatingSystem.IsWindows()
        ? StringComparison.OrdinalIgnoreCase
        : StringComparison.Ordinal;

    private static bool IsInvalidWindowsCharacter(char character) =>
        character is >= '\0' and <= '\u001f' or '<' or '>' or ':' or '"' or '/' or '\\' or '|' or '?' or '*';

    private static string TrimWindowsEnding(string value) => value.TrimEnd(' ', '.');

    private static bool IsReservedDeviceName(string component)
    {
        var firstDot = component.IndexOf('.');
        var name = TrimWindowsEnding(firstDot < 0 ? component : component[..firstDot]);
        return ReservedNames.Contains(name);
    }

    private static HashSet<string> CreateReservedNames()
    {
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "CON", "PRN", "AUX", "NUL", "CONIN$", "CONOUT$"
        };
        for (var index = 1; index <= 9; index++)
        {
            names.Add($"COM{index}");
            names.Add($"LPT{index}");
        }

        foreach (var superscript in new[] { '¹', '²', '³' })
        {
            names.Add($"COM{superscript}");
            names.Add($"LPT{superscript}");
        }

        return names;
    }

    private static string TruncateWithHash(string value, int maximumLength, out bool wasShortened)
    {
        if (value.Length <= maximumLength)
        {
            wasShortened = false;
            return value;
        }

        wasShortened = true;
        if (maximumLength <= 9)
        {
            var shortPrefix = TakeTextElements(value, maximumLength);
            return shortPrefix.Length == 0 ? "_" : shortPrefix;
        }

        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value)))[..8];
        var suffix = "~" + hash;
        var prefix = TakeTextElements(value, maximumLength - suffix.Length);
        return prefix + suffix;
    }

    private static string TakeTextElements(string value, int maximumCodeUnits)
    {
        var builder = new StringBuilder(Math.Min(value.Length, maximumCodeUnits));
        var enumerator = StringInfo.GetTextElementEnumerator(value);
        while (enumerator.MoveNext())
        {
            var textElement = enumerator.GetTextElement();
            if (builder.Length + textElement.Length > maximumCodeUnits)
            {
                break;
            }

            builder.Append(textElement);
        }

        return builder.ToString();
    }

    private static int GetMaximumPathLength() => OperatingSystem.IsWindows() ? WindowsMaxPathLength : 32759;

    private static string TrimEndingSeparators(string path)
    {
        var root = Path.GetPathRoot(path) ?? string.Empty;
        var trimmed = path;
        while (trimmed.Length > root.Length &&
               (trimmed[^1] == Path.DirectorySeparatorChar || trimmed[^1] == Path.AltDirectorySeparatorChar))
        {
            trimmed = trimmed[..^1];
        }

        return trimmed;
    }
}
