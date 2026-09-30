namespace Ncm.Core;

public static class OutputPlanner
{
    public static OutputPlan Create(
        string outputDirectory,
        IReadOnlyList<OutputPlanItem> items,
        OutputPlanOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(items);
        options ??= new OutputPlanOptions();
        ValidateOptions(options);

        var rootPath = OutputPathSecurity.NormalizeRoot(outputDirectory);
        var fileNames = BuildFileNames(items, options, rootPath);
        var planned = new List<PlannedOutputItem>(fileNames.Count);
        foreach (var file in fileNames)
        {
            var targetDirectory = file.Item.TargetDirectory
                ?? throw new InvalidOperationException("输出规划缺少目标目录。");
            var targetPath = Path.GetFullPath(Path.Combine(targetDirectory, file.FileName));
            OutputPathSecurity.EnsureContained(rootPath, targetPath);
            EnsurePathLength(targetPath);

            planned.Add(new PlannedOutputItem(
                file.Item.Source,
                file.Item.GroupName,
                file.Item.Title,
                file.Item.Artists,
                file.Item.Album,
                targetDirectory,
                file.RequestedFileName,
                file.FileName,
                targetPath,
                file.DiscNumber,
                file.TrackNumber,
                file.OrderInGroup,
                file.Item.OrderBasis,
                file.NameWasShortened || file.Item.DirectoryWasShortened));
        }

        return new OutputPlan(rootPath, options, Array.AsReadOnly(planned.ToArray()));
    }

    public static IReadOnlyList<OutputFileNamePreview> PreviewFileNames(
        IReadOnlyList<OutputPlanItem> items,
        OutputPlanOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(items);
        options ??= new OutputPlanOptions();
        ValidateOptions(options);

        var fileNames = BuildFileNames(items, options, rootPath: null);
        return Array.AsReadOnly(fileNames.Select(static file => new OutputFileNamePreview(
            file.Item.Source.InputPath,
            file.FileName,
            file.DiscNumber,
            file.TrackNumber,
            file.Item.OrderBasis)).ToArray());
    }

    private static List<PlannedFileName> BuildFileNames(
        IReadOnlyList<OutputPlanItem> items,
        OutputPlanOptions options,
        string? rootPath)
    {
        var prepared = items.Select((item, index) => Prepare(item, index, rootPath, options)).ToArray();
        var groupAcrossDirectories = options.NumberTracks &&
            options.TrackNumbering == OutputTrackNumbering.AlbumOrder;
        if (groupAcrossDirectories)
        {
            ValidateAlbumIdentities(prepared);
        }
        var groups = GroupItems(prepared, groupAcrossDirectories);
        var ordered = OrderItems(prepared, groups, options);
        var usedNames = new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase);
        var planned = new List<PlannedFileName>(prepared.Length);
        foreach (var orderedItem in ordered)
        {
            var item = orderedItem.Item;
            var trackNumber = options.NumberTracks ? item.PlannedTrackNumber : null;
            var discNumber = options.NumberTracks ? item.PlannedDiscNumber : null;
            var prefix = options.NumberTracks
                ? FormatTrackPrefix(item.UsesMultiDiscPrefix, discNumber, trackNumber)
                : string.Empty;
            var namedFile = prefix + BuildRequestedName(item, options.FileNaming);
            var extension = OutputPathSecurity.GetExtension(item.Source.OutputFormat);
            var nameWasShortened = false;
            var requestedFileName = item.TargetDirectory is { } targetDirectory
                ? OutputPathSecurity.SanitizeFileName(
                    namedFile,
                    prefix + item.SourceFileName,
                    extension,
                    targetDirectory,
                    out nameWasShortened)
                : SanitizePreviewFileName(
                    namedFile,
                    prefix + item.SourceFileName,
                    extension,
                    out nameWasShortened);
            var fileName = AllocatePreviewName(
                item.DirectoryKey,
                requestedFileName,
                usedNames,
                item.TargetDirectory,
                out _);

            planned.Add(new PlannedFileName(
                item,
                requestedFileName,
                fileName,
                discNumber,
                trackNumber,
                orderedItem.OrderInGroup,
                nameWasShortened));
        }

        return planned;
    }

    private static PreparedItem Prepare(
        OutputPlanItem source,
        int inputIndex,
        string? rootPath,
        OutputPlanOptions options)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentException.ThrowIfNullOrWhiteSpace(source.InputPath);

        var sourceFileName = Path.GetFileNameWithoutExtension(source.InputPath);
        if (string.IsNullOrWhiteSpace(sourceFileName))
        {
            sourceFileName = "未命名";
        }

        var title = string.IsNullOrWhiteSpace(source.Title) ? sourceFileName : source.Title;
        var artists = source.Artists is null
            ? []
            : source.Artists.Where(static artist => !string.IsNullOrWhiteSpace(artist)).ToArray();
        if (artists.Length == 0)
        {
            artists = ["未知歌手"];
        }

        var album = string.IsNullOrWhiteSpace(source.Album)
            ? options.Classification == OutputClassification.Album && !string.IsNullOrWhiteSpace(source.GroupOverride)
                ? source.GroupOverride
                : "未分类"
            : source.Album;
        var groupName = string.IsNullOrWhiteSpace(source.Album) && !string.IsNullOrWhiteSpace(source.GroupOverride)
            ? source.GroupOverride
            : album;

        var directoryName = options.Classification switch
        {
            OutputClassification.None => groupName,
            OutputClassification.Album => album,
            OutputClassification.Artist => artists[0],
            _ => throw new ArgumentOutOfRangeException(nameof(options), "不支持的输出分类方式。")
        };
        string? targetDirectory = null;
        string directoryKey;
        var directoryWasShortened = false;
        if (rootPath is not null)
        {
            targetDirectory = OutputPathSecurity.GetTargetDirectory(
                rootPath,
                options.Classification,
                directoryName,
                out directoryWasShortened);
            OutputPathSecurity.EnsureSafeClassificationDirectory(rootPath, targetDirectory);
            directoryKey = targetDirectory;
        }
        else
        {
            directoryKey = GetPreviewDirectoryKey(options.Classification, directoryName);
        }

        var safeGroupForKey = OutputPathSecurity.SanitizeComponent(groupName, "未分类", 255, out _);
        var groupKey = options.Classification switch
        {
            OutputClassification.Album => "album:" + directoryKey,
            OutputClassification.Artist => "artist:" + directoryKey + "\0" + safeGroupForKey,
            _ => "group:" + safeGroupForKey
        };
        if (options.NumberTracks && options.TrackNumbering == OutputTrackNumbering.AlbumOrder)
        {
            var albumIdentity = string.IsNullOrWhiteSpace(source.AlbumIdentity)
                ? "name:" + groupName
                : "id:" + source.AlbumIdentity;
            groupKey = "album-order:" + albumIdentity;
        }

        return new PreparedItem(
            source,
            inputIndex,
            sourceFileName,
            title,
            Array.AsReadOnly(artists),
            album,
            groupName,
            targetDirectory,
            directoryKey,
            groupKey,
            directoryWasShortened);
    }

    private static string GetPreviewDirectoryKey(OutputClassification classification, string directoryName)
    {
        if (classification == OutputClassification.None)
        {
            return "<root>";
        }

        var safeDirectoryName = OutputPathSecurity.SanitizeComponent(directoryName, "未分类", 255, out _);
        return $"{classification}:{safeDirectoryName}";
    }

    private static void ValidateAlbumIdentities(IEnumerable<PreparedItem> prepared)
    {
        foreach (var album in prepared.GroupBy(static item => item.GroupName, StringComparer.OrdinalIgnoreCase))
        {
            if (!album.Any(static item => string.IsNullOrWhiteSpace(item.Source.AlbumIdentity)))
            {
                continue;
            }

            if (album.Any(static item => !string.IsNullOrWhiteSpace(item.Source.AlbumIdentity)) ||
                album.Select(static item => item.Artists[0].Trim()).Distinct(StringComparer.OrdinalIgnoreCase).Skip(1).Any())
            {
                throw new InvalidOperationException(
                    $"专辑“{album.Key}”缺少完整的专辑 ID 或专辑艺术家信息，无法确定是否属于同一张专辑。请改用“从上到下进行标号”。");
            }
        }
    }

    private static Dictionary<string, List<PreparedItem>> GroupItems(
        IEnumerable<PreparedItem> prepared,
        bool preserveItemDirectories)
    {
        var groups = new Dictionary<string, List<PreparedItem>>(StringComparer.OrdinalIgnoreCase);
        foreach (var item in prepared)
        {
            if (!groups.TryGetValue(item.GroupKey, out var group))
            {
                group = [];
                groups.Add(item.GroupKey, group);
            }

            group.Add(preserveItemDirectories || group.Count == 0
                ? item
                : item with
                {
                    TargetDirectory = group[0].TargetDirectory,
                    DirectoryKey = group[0].DirectoryKey
                });
        }

        return groups;
    }

    private static List<OrderedItem> OrderItems(
        IReadOnlyList<PreparedItem> prepared,
        Dictionary<string, List<PreparedItem>> groups,
        OutputPlanOptions options)
    {
        if (!options.NumberTracks && prepared.All(static item => item.Source.ManualOrder is null))
        {
            var groupOrder = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            var inputOrder = new List<OrderedItem>(prepared.Count);
            foreach (var item in prepared)
            {
                groupOrder.TryGetValue(item.GroupKey, out var orderInGroup);
                orderInGroup++;
                groupOrder[item.GroupKey] = orderInGroup;
                inputOrder.Add(new OrderedItem(item with
                {
                    OrderBasis = OutputOrderBasis.InputOrder,
                    PlannedDiscNumber = null,
                    PlannedTrackNumber = null,
                    UsesMultiDiscPrefix = false
                }, orderInGroup));
            }

            return inputOrder;
        }

        if (options.NumberTracks && options.TrackNumbering == OutputTrackNumbering.ListOrder)
        {
            var groupOrder = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            var listOrder = new List<OrderedItem>(prepared.Count);
            for (var index = 0; index < prepared.Count; index++)
            {
                var item = prepared[index] with
                {
                    OrderBasis = OutputOrderBasis.InputOrder,
                    PlannedDiscNumber = 1u,
                    PlannedTrackNumber = checked((uint)(index + 1)),
                    UsesMultiDiscPrefix = false
                };
                groupOrder.TryGetValue(item.GroupKey, out var orderInGroup);
                orderInGroup++;
                groupOrder[item.GroupKey] = orderInGroup;
                listOrder.Add(new OrderedItem(item, orderInGroup));
            }

            return listOrder;
        }

        var orderedItems = new List<OrderedItem>(prepared.Count);
        var numbering = options.NumberTracks ? options.TrackNumbering : OutputTrackNumbering.Automatic;
        foreach (var group in groups.Values)
        {
            var orderedGroup = OrderGroup(group, numbering, options.NumberTracks);
            for (var index = 0; index < orderedGroup.Count; index++)
            {
                orderedItems.Add(new OrderedItem(orderedGroup[index], index + 1));
            }
        }

        return orderedItems;
    }

    private static List<PreparedItem> OrderGroup(
        List<PreparedItem> group,
        OutputTrackNumbering numbering,
        bool numberTracks)
    {
        var allManual = group.All(static item => item.Source.ManualOrder is > 0) &&
            group.Select(static item => item.Source.ManualOrder).Distinct().Count() == group.Count;
        if (numbering == OutputTrackNumbering.Automatic && allManual)
        {
            var manual = group.OrderBy(static item => item.Source.ManualOrder).ThenBy(static item => item.InputIndex).ToList();
            for (var index = 0; index < manual.Count; index++)
            {
                manual[index] = manual[index] with
                {
                    OrderBasis = OutputOrderBasis.Manual,
                    PlannedDiscNumber = numberTracks ? 1u : null,
                    PlannedTrackNumber = numberTracks ? checked((uint)(index + 1)) : null,
                    UsesMultiDiscPrefix = false
                };
            }

            return manual;
        }

        if (numberTracks && numbering == OutputTrackNumbering.AlbumOrder)
        {
            var error = GetSourceTrackOrderError(group, out var multiDisc);
            if (error is not null)
            {
                var albumName = group[0].GroupName;
                throw new InvalidOperationException(
                    $"专辑“{albumName}”的{error}，无法根据专辑曲序来标号。请修正碟号或曲目号，或改用“从上到下进行标号”。");
            }

            return OrderBySourceTrack(group, multiDisc);
        }

        if (numberTracks && numbering == OutputTrackNumbering.Automatic && HasValidSourceTrackOrder(group, out var automaticMultiDisc))
        {
            return OrderBySourceTrack(group, automaticMultiDisc);
        }

        var natural = group
            .OrderBy(static item => item.SourceFileName, NaturalFileNameComparer.Instance)
            .ThenBy(static item => item.SourceFileName, StringComparer.OrdinalIgnoreCase)
            .ThenBy(static item => item.InputIndex)
            .ToList();
        for (var index = 0; index < natural.Count; index++)
        {
            natural[index] = natural[index] with
            {
                OrderBasis = numberTracks ? OutputOrderBasis.NaturalFileName : OutputOrderBasis.InputOrder,
                PlannedDiscNumber = numberTracks ? 1u : null,
                PlannedTrackNumber = numberTracks ? checked((uint)(index + 1)) : null,
                UsesMultiDiscPrefix = false
            };
        }

        if (!numberTracks)
        {
            return group.OrderBy(static item => item.InputIndex)
                .Select((item, index) => item with
                {
                    OrderBasis = OutputOrderBasis.InputOrder,
                    PlannedDiscNumber = null,
                    PlannedTrackNumber = null,
                    UsesMultiDiscPrefix = false
                })
                .ToList();
        }

        return natural;
    }

    private static List<PreparedItem> OrderBySourceTrack(List<PreparedItem> group, bool multiDisc)
    {
        var sorted = group
            .OrderBy(static item => item.Source.DiscNumber ?? 1)
            .ThenBy(static item => item.Source.TrackNumber)
            .ThenBy(static item => item.InputIndex)
            .ToList();
        for (var index = 0; index < sorted.Count; index++)
        {
            var originalDisc = sorted[index].Source.DiscNumber;
            sorted[index] = sorted[index] with
            {
                OrderBasis = OutputOrderBasis.SourceTrack,
                PlannedDiscNumber = multiDisc ? originalDisc : 1u,
                PlannedTrackNumber = sorted[index].Source.TrackNumber,
                UsesMultiDiscPrefix = multiDisc
            };
        }

        return sorted;
    }

    private static bool HasValidSourceTrackOrder(List<PreparedItem> group, out bool multiDisc)
    {
        return GetSourceTrackOrderError(group, out multiDisc) is null;
    }

    private static string? GetSourceTrackOrderError(List<PreparedItem> group, out bool multiDisc)
    {
        multiDisc = false;
        if (group.Any(static item => item.Source.TrackNumber is not > 0))
        {
            return "缺少有效曲目号";
        }

        var hasAnyDisc = group.Any(static item => item.Source.DiscNumber.HasValue);
        var hasMissingDisc = group.Any(static item => !item.Source.DiscNumber.HasValue);
        if (hasAnyDisc && hasMissingDisc)
        {
            return "碟号不完整";
        }

        if (hasAnyDisc && group.Any(static item => item.Source.DiscNumber is 0))
        {
            return "包含无效碟号";
        }

        var discs = hasAnyDisc
            ? group.Select(static item => item.Source.DiscNumber!.Value).Distinct().Order().ToArray()
            : [1u];
        multiDisc = discs.Length > 1;
        if (hasAnyDisc && discs.Length == 1 && discs[0] != 1)
        {
            return "碟号必须从 1 开始";
        }

        if (multiDisc && !discs.SequenceEqual(Enumerable.Range(1, discs.Length).Select(static value => (uint)value)))
        {
            return "碟号不连续";
        }

        if (group
            .GroupBy(static item => item.Source.DiscNumber ?? 1)
            .Any(static discGroup => discGroup.Select(static item => item.Source.TrackNumber).Distinct().Count() != discGroup.Count()))
        {
            return "存在重复曲目号";
        }

        return null;
    }

    private static string BuildRequestedName(PreparedItem item, OutputFileNaming naming) => naming switch
    {
        OutputFileNaming.SourceFileName => item.SourceFileName,
        OutputFileNaming.ArtistTitle => string.Join('、', item.Artists.Select(TrimMetadataEnding)) + " - " + TrimMetadataEnding(item.Title),
        OutputFileNaming.TitleArtist => TrimMetadataEnding(item.Title) + " - " + string.Join('、', item.Artists.Select(TrimMetadataEnding)),
        _ => throw new ArgumentOutOfRangeException(nameof(naming), naming, "不支持的输出文件命名方式。")
    };

    private static string TrimMetadataEnding(string value) => value.TrimEnd(' ', '.');

    private static string FormatTrackPrefix(bool multiDisc, uint? discNumber, uint? trackNumber)
    {
        if (trackNumber is null or 0)
        {
            throw new InvalidOperationException("已启用曲序编号，但规划结果缺少有效曲序号。");
        }

        var track = trackNumber.Value.ToString("D2", System.Globalization.CultureInfo.InvariantCulture);
        return multiDisc
            ? (discNumber ?? throw new InvalidOperationException("多碟规划结果缺少碟号。"))
                .ToString(System.Globalization.CultureInfo.InvariantCulture) + "-" + track + " - "
            : track + " - ";
    }

    private static string SanitizePreviewFileName(
        string rawName,
        string fallback,
        string extension,
        out bool shortened)
    {
        const int componentLength = 255;
        const int collisionSuffixReserve = 24;
        var maximumLength = componentLength - collisionSuffixReserve;
        var requested = OutputPathSecurity.SanitizeComponent(
            rawName,
            fallback,
            maximumLength - extension.Length,
            out shortened);
        if (requested.Length == 0)
        {
            requested = OutputPathSecurity.SanitizeComponent(
                fallback,
                "未命名",
                maximumLength - extension.Length,
                out shortened);
        }

        return requested + extension;
    }

    private static string AllocatePreviewName(
        string directoryKey,
        string requestedName,
        Dictionary<string, HashSet<string>> usedNames,
        string? targetDirectory,
        out int collisionIndex)
    {
        if (!usedNames.TryGetValue(directoryKey, out var used))
        {
            used = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            usedNames.Add(directoryKey, used);
        }

        collisionIndex = 1;
        while (true)
        {
            var candidate = OutputPathSecurity.AddCollisionSuffix(requestedName, collisionIndex);
            var existsOnDisk = targetDirectory is not null &&
                FindExistingCaseInsensitiveFileOrDirectory(targetDirectory, candidate);
            if (!existsOnDisk && !used.Contains(candidate))
            {
                used.Add(candidate);
                return candidate;
            }

            collisionIndex++;
        }
    }

    private static bool FindExistingCaseInsensitiveFileOrDirectory(string parentDirectory, string name)
    {
        if (!Directory.Exists(parentDirectory))
        {
            return false;
        }

        return Directory.EnumerateFileSystemEntries(parentDirectory)
            .Any(entry => string.Equals(Path.GetFileName(entry), name, StringComparison.OrdinalIgnoreCase));
    }

    private static void EnsurePathLength(string targetPath)
    {
        if (OperatingSystem.IsWindows() && targetPath.Length > 259)
        {
            throw new PathTooLongException($"输出路径超过 Windows 当前配置可用的长度：{targetPath}");
        }
    }

    private static void ValidateOptions(OutputPlanOptions options)
    {
        if (!Enum.IsDefined(options.Classification))
        {
            throw new ArgumentOutOfRangeException(nameof(options), options.Classification, "不支持的输出分类方式。");
        }

        if (!Enum.IsDefined(options.FileNaming))
        {
            throw new ArgumentOutOfRangeException(nameof(options), options.FileNaming, "不支持的输出文件命名方式。");
        }

        if (!Enum.IsDefined(options.TrackNumbering))
        {
            throw new ArgumentOutOfRangeException(nameof(options), options.TrackNumbering, "不支持的曲序编号方式。");
        }
    }

    private sealed record PreparedItem(
        OutputPlanItem Source,
        int InputIndex,
        string SourceFileName,
        string Title,
        IReadOnlyList<string> Artists,
        string Album,
        string GroupName,
        string? TargetDirectory,
        string DirectoryKey,
        string GroupKey,
        bool DirectoryWasShortened,
        OutputOrderBasis OrderBasis = OutputOrderBasis.InputOrder,
        uint? PlannedDiscNumber = null,
        uint? PlannedTrackNumber = null,
        bool UsesMultiDiscPrefix = false);

    private sealed record OrderedItem(PreparedItem Item, int OrderInGroup);

    private sealed record PlannedFileName(
        PreparedItem Item,
        string RequestedFileName,
        string FileName,
        uint? DiscNumber,
        uint? TrackNumber,
        int OrderInGroup,
        bool NameWasShortened);

    private sealed class NaturalFileNameComparer : IComparer<string>
    {
        public static NaturalFileNameComparer Instance { get; } = new();

        public int Compare(string? left, string? right)
        {
            if (ReferenceEquals(left, right))
            {
                return 0;
            }

            if (left is null)
            {
                return -1;
            }

            if (right is null)
            {
                return 1;
            }

            var leftIndex = 0;
            var rightIndex = 0;
            while (leftIndex < left.Length && rightIndex < right.Length)
            {
                var leftIsDigit = IsAsciiDigit(left[leftIndex]);
                var rightIsDigit = IsAsciiDigit(right[rightIndex]);
                if (leftIsDigit && rightIsDigit)
                {
                    var leftEnd = ReadDigitRun(left, leftIndex);
                    var rightEnd = ReadDigitRun(right, rightIndex);
                    var leftSignificant = SkipLeadingZeros(left, leftIndex, leftEnd);
                    var rightSignificant = SkipLeadingZeros(right, rightIndex, rightEnd);
                    var leftSignificantLength = leftEnd - leftSignificant;
                    var rightSignificantLength = rightEnd - rightSignificant;
                    var numberComparison = leftSignificantLength.CompareTo(rightSignificantLength);
                    if (numberComparison != 0)
                    {
                        return numberComparison;
                    }

                    for (var offset = 0; offset < leftSignificantLength; offset++)
                    {
                        numberComparison = left[leftSignificant + offset].CompareTo(right[rightSignificant + offset]);
                        if (numberComparison != 0)
                        {
                            return numberComparison;
                        }
                    }

                    var leadingZeroComparison = (leftEnd - leftIndex).CompareTo(rightEnd - rightIndex);
                    if (leadingZeroComparison != 0)
                    {
                        return leadingZeroComparison;
                    }

                    leftIndex = leftEnd;
                    rightIndex = rightEnd;
                    continue;
                }

                var charComparison = char.ToUpperInvariant(left[leftIndex]).CompareTo(char.ToUpperInvariant(right[rightIndex]));
                if (charComparison != 0)
                {
                    return charComparison;
                }

                leftIndex++;
                rightIndex++;
            }

            return (left.Length - leftIndex).CompareTo(right.Length - rightIndex);
        }

        private static bool IsAsciiDigit(char character) => character is >= '0' and <= '9';

        private static int ReadDigitRun(string value, int start)
        {
            var index = start;
            while (index < value.Length && IsAsciiDigit(value[index]))
            {
                index++;
            }

            return index;
        }

        private static int SkipLeadingZeros(string value, int start, int end)
        {
            while (start < end - 1 && value[start] == '0')
            {
                start++;
            }

            return start;
        }
    }
}
