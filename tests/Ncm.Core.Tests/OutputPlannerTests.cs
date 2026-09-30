using System.Diagnostics;
using System.Text;
using Ncm.Core;

namespace Ncm.Core.Tests;

public sealed class OutputPlannerTests
{
    [Theory]
    [InlineData(OutputClassification.None, "", "Album")]
    [InlineData(OutputClassification.Album, "Album", "Album")]
    [InlineData(OutputClassification.Artist, "First Artist", "Album")]
    public void ClassificationUsesSafeGroupFolders(
        OutputClassification classification,
        string expectedRelativeDirectory,
        string expectedGroup)
    {
        using var temporaryDirectory = new TemporaryDirectory();
        var sourcePath = Path.Combine(temporaryDirectory.FullName, "source.ncm");
        var item = Item(sourcePath, title: "Title", artists: ["First Artist", "Second Artist"], album: "Album");

        var plan = OutputPlanner.Create(
            temporaryDirectory.FullName,
            [item],
            new OutputPlanOptions(classification, OutputFileNaming.ArtistTitle));
        var planned = Assert.Single(plan.Items);

        var expectedDirectory = classification == OutputClassification.None
            ? temporaryDirectory.FullName
            : Path.Combine(temporaryDirectory.FullName, expectedRelativeDirectory);
        Assert.Equal(Path.GetFullPath(expectedDirectory), planned.TargetDirectory);
        Assert.Equal(expectedGroup, planned.GroupName);
        Assert.Equal("First Artist、Second Artist - Title.mp3", planned.FileName);
    }

    [Fact]
    public void KeepsUnicodeAndCleansInvalidAndReservedCharacters()
    {
        using var temporaryDirectory = new TemporaryDirectory();
        var item = Item(
            Path.Combine(temporaryDirectory.FullName, "ignored.ncm"),
            title: "歌曲 日本 é 😀 <bad>:name. ",
            artists: ["歌手"]);

        var plan = OutputPlanner.Create(
            temporaryDirectory.FullName,
            [item],
            new OutputPlanOptions(FileNaming: OutputFileNaming.TitleArtist));

        var path = Assert.Single(plan.Items).TargetPath;
        Assert.Equal("歌曲 日本 é 😀 _bad__name - 歌手.mp3", Path.GetFileName(path));
        Assert.Contains("歌曲", path, StringComparison.Ordinal);
        Assert.Contains("😀", path, StringComparison.Ordinal);

        foreach (var (sourceName, expectedName) in new[]
        {
            ("CON.ncm", "_CON.mp3"),
            ("COM1.ncm", "_COM1.mp3"),
            ("LPT¹.ncm", "_LPT¹.mp3")
        })
        {
            var reservedPlan = OutputPlanner.Create(
                temporaryDirectory.FullName,
                [Item(Path.Combine(temporaryDirectory.FullName, sourceName))]);
            Assert.Equal(expectedName, Assert.Single(reservedPlan.Items).FileName);
        }
    }

    [Fact]
    public void GroupOverrideReplacesMissingAlbumForFolderAndTag()
    {
        using var temporaryDirectory = new TemporaryDirectory();
        var item = Item(
            Path.Combine(temporaryDirectory.FullName, "track.ncm"),
            title: "Title",
            album: null,
            groupOverride: "手动专辑");

        var plan = OutputPlanner.Create(
            temporaryDirectory.FullName,
            [item],
            new OutputPlanOptions(OutputClassification.Album));

        var planned = Assert.Single(plan.Items);
        Assert.Equal("手动专辑", planned.GroupName);
        Assert.Equal("手动专辑", planned.Album);
        Assert.Equal(Path.Combine(temporaryDirectory.FullName, "手动专辑"), planned.TargetDirectory);
    }

    [Fact]
    public void MetadataPathTraversalCannotEscapeOutputRoot()
    {
        using var temporaryDirectory = new TemporaryDirectory();
        var root = Path.Combine(temporaryDirectory.FullName, "out");
        var item = Item(
            Path.Combine(temporaryDirectory.FullName, "track.ncm"),
            title: "..\\..\\outside",
            album: "..\\..\\outside");

        var plan = OutputPlanner.Create(root, [item], new OutputPlanOptions(OutputClassification.Album));
        var planned = Assert.Single(plan.Items);
        var relativeDirectory = Path.GetRelativePath(root, planned.TargetDirectory);
        var relativeFile = Path.GetRelativePath(root, planned.TargetPath);

        Assert.NotEqual("..", relativeDirectory);
        Assert.False(relativeDirectory.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal));
        Assert.NotEqual("..", relativeFile);
        Assert.False(relativeFile.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal));
        Assert.StartsWith(root, planned.TargetPath, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void PreviewReservesExistingAndPlannedNamesIgnoringCase()
    {
        using var temporaryDirectory = new TemporaryDirectory();
        File.WriteAllText(Path.Combine(temporaryDirectory.FullName, "SONG.MP3"), "existing");
        var first = Item(Path.Combine(temporaryDirectory.FullName, "song.ncm"));
        var second = Item(Path.Combine(temporaryDirectory.FullName, "sub", "song.ncm"));

        var plan = OutputPlanner.Create(temporaryDirectory.FullName, [first, second]);

        Assert.Equal(["song (2).mp3", "song (3).mp3"], plan.Items.Select(static item => item.FileName));
    }

    [Fact]
    public void SourceTracksKeepDiscTrackTagsAndUseMultiDiscPrefixes()
    {
        using var temporaryDirectory = new TemporaryDirectory();
        var items = new[]
        {
            Item(Path.Combine(temporaryDirectory.FullName, "two.ncm"), title: "two", album: "Album", disc: 2, track: 1),
            Item(Path.Combine(temporaryDirectory.FullName, "ten.ncm"), title: "ten", album: "Album", disc: 1, track: 10),
            Item(Path.Combine(temporaryDirectory.FullName, "one.ncm"), title: "one", album: "Album", disc: 1, track: 2)
        };

        var plan = OutputPlanner.Create(
            temporaryDirectory.FullName,
            items,
            new OutputPlanOptions(NumberTracks: true));

        Assert.Equal(["1-02 - one.mp3", "1-10 - ten.mp3", "2-01 - two.mp3"], plan.Items.Select(static item => item.FileName));
        Assert.Equal(new[] { (1u, 2u), (1u, 10u), (2u, 1u) }, plan.Items.Select(static item => (item.WrittenDiscNumber!.Value, item.WrittenTrackNumber!.Value)));
        Assert.All(plan.Items, static item => Assert.Equal(OutputOrderBasis.SourceTrack, item.OrderBasis));
    }

    [Fact]
    public void ListOrderNumbersInterleavedAlbumsContinuouslyInInputOrder()
    {
        using var temporaryDirectory = new TemporaryDirectory();
        var items = new[]
        {
            Item(Path.Combine(temporaryDirectory.FullName, "first.ncm"), album: "Album A", disc: 2, track: 8),
            Item(Path.Combine(temporaryDirectory.FullName, "second.ncm"), album: "Album B", disc: 1, track: 1),
            Item(Path.Combine(temporaryDirectory.FullName, "third.ncm"), album: "Album A", disc: 1, track: 2)
        };

        var plan = OutputPlanner.Create(
            temporaryDirectory.FullName,
            items,
            new OutputPlanOptions(
                Classification: OutputClassification.Album,
                NumberTracks: true,
                TrackNumbering: OutputTrackNumbering.ListOrder));

        Assert.Equal(["01 - first.mp3", "02 - second.mp3", "03 - third.mp3"], plan.Items.Select(static item => item.FileName));
        Assert.Equal(["Album A", "Album B", "Album A"], plan.Items.Select(static item => item.Album));
        Assert.Equal([1u, 2u, 3u], plan.Items.Select(static item => item.WrittenTrackNumber!.Value));
        Assert.All(plan.Items, static item => Assert.Equal(1u, item.WrittenDiscNumber));
        Assert.All(plan.Items, static item => Assert.Equal(OutputOrderBasis.InputOrder, item.OrderBasis));
    }

    [Fact]
    public void AlbumOrderUsesValidatedMultiDiscSourceOrder()
    {
        using var temporaryDirectory = new TemporaryDirectory();
        var items = new[]
        {
            Item(Path.Combine(temporaryDirectory.FullName, "disc2.ncm"), album: "Album", disc: 2, track: 1),
            Item(Path.Combine(temporaryDirectory.FullName, "disc1track2.ncm"), album: "Album", disc: 1, track: 2),
            Item(Path.Combine(temporaryDirectory.FullName, "disc1track1.ncm"), album: "Album", disc: 1, track: 1)
        };

        var plan = OutputPlanner.Create(
            temporaryDirectory.FullName,
            items,
            new OutputPlanOptions(NumberTracks: true, TrackNumbering: OutputTrackNumbering.AlbumOrder));

        Assert.Equal(
            ["1-01 - disc1track1.mp3", "1-02 - disc1track2.mp3", "2-01 - disc2.mp3"],
            plan.Items.Select(static item => item.FileName));
        Assert.Equal(new[] { (1u, 1u), (1u, 2u), (2u, 1u) },
            plan.Items.Select(static item => (item.WrittenDiscNumber!.Value, item.WrittenTrackNumber!.Value)));
        Assert.All(plan.Items, static item => Assert.Equal(OutputOrderBasis.SourceTrack, item.OrderBasis));
    }

    [Theory]
    [InlineData(OutputClassification.None)]
    [InlineData(OutputClassification.Album)]
    public void AlbumOrderSeparatesSameNamedAlbumsByIdentity(
        OutputClassification classification)
    {
        using var temporaryDirectory = new TemporaryDirectory();
        var items = new[]
        {
            Item(Path.Combine(temporaryDirectory.FullName, "first.ncm"), album: "Compilation", track: 1, albumIdentity: "album-id-1"),
            Item(Path.Combine(temporaryDirectory.FullName, "second.ncm"), album: "Compilation", track: 1, albumIdentity: "album-id-2")
        };

        var plan = OutputPlanner.Create(
            temporaryDirectory.FullName,
            items,
            new OutputPlanOptions(
                Classification: classification,
                NumberTracks: true,
                TrackNumbering: OutputTrackNumbering.AlbumOrder));

        Assert.Equal([1u, 1u], plan.Items.Select(static item => item.WrittenTrackNumber!.Value));
        Assert.All(plan.Items, static item => Assert.Equal(OutputOrderBasis.SourceTrack, item.OrderBasis));
    }

    [Fact]
    public void AlbumOrderKeepsSameIdentityTracksTogetherAcrossArtists()
    {
        using var temporaryDirectory = new TemporaryDirectory();
        var items = new[]
        {
            Item(Path.Combine(temporaryDirectory.FullName, "first.ncm"), artists: ["Artist One"], album: "Compilation", track: 1, albumIdentity: "album-id"),
            Item(Path.Combine(temporaryDirectory.FullName, "second.ncm"), artists: ["Artist Two"], album: "Compilation", track: 1, albumIdentity: "album-id")
        };

        var error = Assert.Throws<InvalidOperationException>(() => OutputPlanner.Create(
            temporaryDirectory.FullName,
            items,
            new OutputPlanOptions(
                Classification: OutputClassification.Album,
                NumberTracks: true,
                TrackNumbering: OutputTrackNumbering.AlbumOrder)));

        Assert.Contains("Compilation", error.Message, StringComparison.Ordinal);
        Assert.Contains("重复曲目号", error.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(OutputClassification.None)]
    [InlineData(OutputClassification.Album)]
    [InlineData(OutputClassification.Artist)]
    public void AlbumOrderRejectsAmbiguousSameNamedAlbumsAcrossArtists(OutputClassification classification)
    {
        using var temporaryDirectory = new TemporaryDirectory();
        var items = new[]
        {
            Item(Path.Combine(temporaryDirectory.FullName, "first.ncm"), artists: ["Artist One"], album: "Shared Name", track: 1),
            Item(Path.Combine(temporaryDirectory.FullName, "second.ncm"), artists: ["Artist Two"], album: "Shared Name", track: 2)
        };
        var options = new OutputPlanOptions(Classification: classification,
            NumberTracks: true, TrackNumbering: OutputTrackNumbering.AlbumOrder);

        var previewError = Assert.Throws<InvalidOperationException>(() => OutputPlanner.PreviewFileNames(items, options));
        var planError = Assert.Throws<InvalidOperationException>(() => OutputPlanner.Create(temporaryDirectory.FullName, items, options));

        Assert.Equal(previewError.Message, planError.Message);
        Assert.Contains("无法确定是否属于同一张专辑", planError.Message);
        Assert.Contains("从上到下进行标号", planError.Message);
        var listPlan = OutputPlanner.Create(temporaryDirectory.FullName, items, options with { TrackNumbering = OutputTrackNumbering.ListOrder });
        Assert.Equal([1u, 2u], listPlan.Items.Select(static item => item.WrittenTrackNumber!.Value));
    }

    [Fact]
    public void AlbumOrderRejectsMissingIdentityMixedWithKnownIdentity()
    {
        using var temporaryDirectory = new TemporaryDirectory();
        var items = new[]
        {
            Item(Path.Combine(temporaryDirectory.FullName, "known.ncm"), album: "Shared Name", track: 1, albumIdentity: "known-album"),
            Item(Path.Combine(temporaryDirectory.FullName, "unknown.ncm"), album: "Shared Name", track: 2)
        };
        var options = new OutputPlanOptions(NumberTracks: true, TrackNumbering: OutputTrackNumbering.AlbumOrder);

        var error = Assert.Throws<InvalidOperationException>(() => OutputPlanner.PreviewFileNames(items, options));

        Assert.Contains("缺少完整的专辑 ID", error.Message);
        Assert.Contains("从上到下进行标号", error.Message);
    }

    [Fact]
    public void AlbumOrderSortsSameIdentityAcrossArtistFoldersAndKeepsEachTargetDirectory()
    {
        using var temporaryDirectory = new TemporaryDirectory();
        var items = new[]
        {
            Item(Path.Combine(temporaryDirectory.FullName, "disc2.ncm"), artists: ["Artist Two"], album: "Compilation", disc: 2, track: 1, albumIdentity: "album-id"),
            Item(Path.Combine(temporaryDirectory.FullName, "disc1track2.ncm"), artists: ["Artist One"], album: "Compilation", disc: 1, track: 2, albumIdentity: "album-id"),
            Item(Path.Combine(temporaryDirectory.FullName, "disc1track1.ncm"), artists: ["Artist One"], album: "Compilation", disc: 1, track: 1, albumIdentity: "album-id")
        };

        var plan = OutputPlanner.Create(
            temporaryDirectory.FullName,
            items,
            new OutputPlanOptions(
                Classification: OutputClassification.Artist,
                NumberTracks: true,
                TrackNumbering: OutputTrackNumbering.AlbumOrder));

        Assert.Equal(
            ["1-01 - disc1track1.mp3", "1-02 - disc1track2.mp3", "2-01 - disc2.mp3"],
            plan.Items.Select(static item => item.FileName));
        Assert.Equal(
            ["Artist One", "Artist One", "Artist Two"],
            plan.Items.Select(static item => Path.GetFileName(item.TargetDirectory)));
        Assert.Equal(new[] { (1u, 1u), (1u, 2u), (2u, 1u) },
            plan.Items.Select(static item => (item.WrittenDiscNumber!.Value, item.WrittenTrackNumber!.Value)));
    }

    [Fact]
    public void AlbumOrderRejectsDuplicateTrackNumbersAcrossArtistFolders()
    {
        using var temporaryDirectory = new TemporaryDirectory();
        var items = new[]
        {
            Item(Path.Combine(temporaryDirectory.FullName, "first.ncm"), artists: ["Artist One"], album: "Compilation", track: 1, albumIdentity: "album-id"),
            Item(Path.Combine(temporaryDirectory.FullName, "second.ncm"), artists: ["Artist Two"], album: "Compilation", track: 1, albumIdentity: "album-id")
        };

        var error = Assert.Throws<InvalidOperationException>(() => OutputPlanner.Create(
            temporaryDirectory.FullName,
            items,
            new OutputPlanOptions(
                Classification: OutputClassification.Artist,
                NumberTracks: true,
                TrackNumbering: OutputTrackNumbering.AlbumOrder)));

        Assert.Contains("Compilation", error.Message, StringComparison.Ordinal);
        Assert.Contains("重复曲目号", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void UnnumberedInterleavedAlbumsKeepInputOrderInPreviewAndCreate()
    {
        using var temporaryDirectory = new TemporaryDirectory();
        var items = new[]
        {
            Item(Path.Combine(temporaryDirectory.FullName, "first-a.ncm"), album: "Album A"),
            Item(Path.Combine(temporaryDirectory.FullName, "middle-b.ncm"), album: "Album B"),
            Item(Path.Combine(temporaryDirectory.FullName, "last-a.ncm"), album: "Album A")
        };
        var options = new OutputPlanOptions(Classification: OutputClassification.Album);

        var preview = OutputPlanner.PreviewFileNames(items, options);
        var plan = OutputPlanner.Create(Path.Combine(temporaryDirectory.FullName, "out"), items, options);

        Assert.Equal(["first-a.mp3", "middle-b.mp3", "last-a.mp3"], preview.Select(static item => item.FileName));
        Assert.Equal(items.Select(static item => item.InputPath), preview.Select(static item => item.InputPath));
        Assert.Equal(items.Select(static item => item.InputPath), plan.Items.Select(static item => item.Source.InputPath));
        Assert.All(preview, static item => Assert.Null(item.WrittenTrackNumber));
    }

    [Fact]
    public void AlbumOrderRejectsMissingTrackNumberWithAlbumAndListOrderSuggestion()
    {
        using var temporaryDirectory = new TemporaryDirectory();
        var items = new[]
        {
            Item(Path.Combine(temporaryDirectory.FullName, "one.ncm"), album: "Missing Track Album", track: 1),
            Item(Path.Combine(temporaryDirectory.FullName, "two.ncm"), album: "Missing Track Album")
        };

        var error = Assert.Throws<InvalidOperationException>(() => OutputPlanner.Create(
            temporaryDirectory.FullName,
            items,
            new OutputPlanOptions(NumberTracks: true, TrackNumbering: OutputTrackNumbering.AlbumOrder)));

        Assert.Contains("Missing Track Album", error.Message, StringComparison.Ordinal);
        Assert.Contains("曲目号", error.Message, StringComparison.Ordinal);
        Assert.Contains("从上到下进行标号", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void AlbumOrderRejectsDuplicateTrackNumberWithAlbumAndListOrderSuggestion()
    {
        using var temporaryDirectory = new TemporaryDirectory();
        var items = new[]
        {
            Item(Path.Combine(temporaryDirectory.FullName, "one.ncm"), album: "Conflicted Album", track: 1),
            Item(Path.Combine(temporaryDirectory.FullName, "two.ncm"), album: "Conflicted Album", track: 1)
        };

        var error = Assert.Throws<InvalidOperationException>(() => OutputPlanner.Create(
            temporaryDirectory.FullName,
            items,
            new OutputPlanOptions(NumberTracks: true, TrackNumbering: OutputTrackNumbering.AlbumOrder)));

        Assert.Contains("Conflicted Album", error.Message, StringComparison.Ordinal);
        Assert.Contains("重复曲目号", error.Message, StringComparison.Ordinal);
        Assert.Contains("从上到下进行标号", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void AlbumOrderRejectsIncompleteDiscNumbersWithAlbumAndListOrderSuggestion()
    {
        using var temporaryDirectory = new TemporaryDirectory();
        var items = new[]
        {
            Item(Path.Combine(temporaryDirectory.FullName, "disc1.ncm"), album: "Incomplete Disc Album", disc: 1, track: 1),
            Item(Path.Combine(temporaryDirectory.FullName, "disc2.ncm"), album: "Incomplete Disc Album", track: 2)
        };

        var error = Assert.Throws<InvalidOperationException>(() => OutputPlanner.Create(
            temporaryDirectory.FullName,
            items,
            new OutputPlanOptions(NumberTracks: true, TrackNumbering: OutputTrackNumbering.AlbumOrder)));

        Assert.Contains("Incomplete Disc Album", error.Message, StringComparison.Ordinal);
        Assert.Contains("碟号不完整", error.Message, StringComparison.Ordinal);
        Assert.Contains("从上到下进行标号", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void FileNamePreviewUsesNamingAndInMemoryCollisionRulesWithoutCreatingOutputDirectory()
    {
        using var temporaryDirectory = new TemporaryDirectory();
        var outputDirectory = Path.Combine(temporaryDirectory.FullName, "out");
        var items = new[]
        {
            Item(Path.Combine(temporaryDirectory.FullName, "first.ncm"), title: "Same Title", album: "Album"),
            Item(Path.Combine(temporaryDirectory.FullName, "second.ncm"), title: "Same Title", album: "Album")
        };
        var options = new OutputPlanOptions(FileNaming: OutputFileNaming.TitleArtist);

        var preview = OutputPlanner.PreviewFileNames(items, options);

        Assert.False(Directory.Exists(outputDirectory));
        Assert.Equal(["Same Title - Artist.mp3", "Same Title - Artist (2).mp3"],
            preview.Select(static item => item.FileName));
        Assert.Equal(items.Select(static item => item.InputPath), preview.Select(static item => item.InputPath));
        Assert.All(preview, static item => Assert.Null(item.WrittenTrackNumber));

        var plan = OutputPlanner.Create(outputDirectory, items, options);

        Assert.Equal(preview.Select(static item => item.FileName), plan.Items.Select(static item => item.FileName));
        Assert.False(Directory.Exists(outputDirectory));
    }

    [Fact]
    public void DuplicateTrackNumbersFallbackToNaturalOrderAndRenumber()
    {
        using var temporaryDirectory = new TemporaryDirectory();
        var items = new[]
        {
            Item(Path.Combine(temporaryDirectory.FullName, "track10.ncm"), album: "Album", track: 1),
            Item(Path.Combine(temporaryDirectory.FullName, "track2.ncm"), album: "Album", track: 1)
        };

        var plan = OutputPlanner.Create(temporaryDirectory.FullName, items, new OutputPlanOptions(NumberTracks: true));

        Assert.Equal(["01 - track2.mp3", "02 - track10.mp3"], plan.Items.Select(static item => item.FileName));
        Assert.Equal([1u, 2u], plan.Items.Select(static item => item.WrittenTrackNumber!.Value));
        Assert.All(plan.Items, static item => Assert.Equal(1u, item.WrittenDiscNumber));
        Assert.All(plan.Items, static item => Assert.Equal(OutputOrderBasis.NaturalFileName, item.OrderBasis));
    }

    [Fact]
    public void ManualOrderOverridesSourceOrderAndKeepsPrefixTagsAligned()
    {
        using var temporaryDirectory = new TemporaryDirectory();
        var items = new[]
        {
            Item(Path.Combine(temporaryDirectory.FullName, "second.ncm"), title: "second", album: "Album", track: 1, manualOrder: 2),
            Item(Path.Combine(temporaryDirectory.FullName, "first.ncm"), title: "first", album: "Album", track: 2, manualOrder: 1)
        };

        var plan = OutputPlanner.Create(temporaryDirectory.FullName, items, new OutputPlanOptions(NumberTracks: true));

        Assert.Equal(["01 - first.mp3", "02 - second.mp3"], plan.Items.Select(static item => item.FileName));
        Assert.Equal([1u, 2u], plan.Items.Select(static item => item.WrittenTrackNumber!.Value));
        Assert.All(plan.Items, static item => Assert.Equal(1u, item.WrittenDiscNumber));
        Assert.All(plan.Items, static item => Assert.Equal(OutputOrderBasis.Manual, item.OrderBasis));
    }

    [Fact]
    public void LongNamesAreShortenedWithoutSplittingUnicodeTextElements()
    {
        using var temporaryDirectory = new TemporaryDirectory();
        var longTitle = string.Concat(Enumerable.Repeat("歌😀e\u0301", 120));
        var plan = OutputPlanner.Create(
            temporaryDirectory.FullName,
            [Item(Path.Combine(temporaryDirectory.FullName, "long.ncm"), title: longTitle)],
            new OutputPlanOptions(FileNaming: OutputFileNaming.TitleArtist));

        var planned = Assert.Single(plan.Items);
        Assert.True(planned.NameWasShortened);
        Assert.True(planned.FileName.Length <= 255);
        Assert.DoesNotContain("�", planned.FileName, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("CONnect", 3, "_")]
    [InlineData("A.B", 2, "A")]
    [InlineData("😀x", 1, "_")]
    public void ShortenedComponentsRemainValidWindowsNames(string input, int limit, string expected)
    {
        var actual = OutputPathSecurity.SanitizeComponent(input, "未分类", limit, out var shortened);

        Assert.True(shortened);
        Assert.Equal(expected, actual);
    }

    [Fact]
    public void RootThatLeavesNoSpaceForFileAndCollisionSuffixHasClearError()
    {
        using var temporaryDirectory = new TemporaryDirectory();
        var longRoot = Path.Combine(temporaryDirectory.FullName, new string('r', 190));

        Assert.Throws<PathTooLongException>(() => OutputPlanner.Create(
            longRoot,
            [Item(Path.Combine(temporaryDirectory.FullName, "track.ncm"))]));
    }

    [Fact]
    public void CommitRechecksCollisionAndPreservesExistingBytes()
    {
        using var temporaryDirectory = new TemporaryDirectory();
        var plan = OutputPlanner.Create(temporaryDirectory.FullName, [Item(Path.Combine(temporaryDirectory.FullName, "song.ncm"))]);
        var planned = Assert.Single(plan.Items);
        var originalPath = planned.TargetPath;
        var originalBytes = Encoding.UTF8.GetBytes("keep me");
        File.WriteAllBytes(originalPath, originalBytes);
        var temporaryPath = Path.Combine(temporaryDirectory.FullName, "staged.tmp");
        File.WriteAllBytes(temporaryPath, Encoding.UTF8.GetBytes("new audio"));

        var committed = OutputCommitter.Commit(temporaryPath, plan, planned);

        Assert.Equal(2, committed.CollisionIndex);
        Assert.Equal("song (2).mp3", Path.GetFileName(committed.OutputPath));
        Assert.Equal(originalBytes, File.ReadAllBytes(originalPath));
        Assert.Equal("new audio", File.ReadAllText(committed.OutputPath));
        Assert.False(File.Exists(temporaryPath));
    }

    [Fact]
    public void ExistingClassificationJunctionIsRejectedOnActualFileSystem()
    {
        using var temporaryDirectory = new TemporaryDirectory();
        var root = Path.Combine(temporaryDirectory.FullName, "out");
        var external = Path.Combine(temporaryDirectory.FullName, "external");
        var junction = Path.Combine(root, "linked");
        Directory.CreateDirectory(root);
        Directory.CreateDirectory(external);

        CreateJunction(junction, external);

        try
        {
            var item = Item(Path.Combine(temporaryDirectory.FullName, "track.ncm"), album: "linked");
            Assert.Throws<IOException>(() => OutputPlanner.Create(root, [item], new OutputPlanOptions(OutputClassification.Album)));
            Assert.Empty(Directory.EnumerateFileSystemEntries(external));
        }
        finally
        {
            if (Directory.Exists(junction))
            {
                Directory.Delete(junction, recursive: false);
            }
        }
    }

    [Fact]
    public void JunctionAddedAfterPreviewIsRejectedAtCommit()
    {
        using var temporaryDirectory = new TemporaryDirectory();
        var root = Path.Combine(temporaryDirectory.FullName, "out");
        var external = Path.Combine(temporaryDirectory.FullName, "external");
        Directory.CreateDirectory(root);
        Directory.CreateDirectory(external);
        var plan = OutputPlanner.Create(
            root,
            [Item(Path.Combine(temporaryDirectory.FullName, "track.ncm"), album: "linked")],
            new OutputPlanOptions(OutputClassification.Album));
        var junction = Assert.Single(plan.Items).TargetDirectory;
        var staged = Path.Combine(root, "staged.tmp");
        File.WriteAllText(staged, "new audio");
        CreateJunction(junction, external);

        try
        {
            Assert.Throws<IOException>(() => OutputCommitter.Commit(staged, plan, plan.Items[0]));
            Assert.True(File.Exists(staged));
            Assert.Empty(Directory.EnumerateFileSystemEntries(external));
        }
        finally
        {
            if (Directory.Exists(junction))
            {
                Directory.Delete(junction, recursive: false);
            }
        }
    }

    private static void CreateJunction(string junction, string target)
    {
        using var process = Process.Start(new ProcessStartInfo
        {
            FileName = "cmd.exe",
            Arguments = $"/c mklink /J \"{junction}\" \"{target}\"",
            CreateNoWindow = true,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        }) ?? throw new InvalidOperationException("无法启动 cmd.exe 创建目录联接点。");
        var output = process.StandardOutput.ReadToEnd();
        var error = process.StandardError.ReadToEnd();
        process.WaitForExit();
        Assert.True(process.ExitCode == 0, $"无法创建目录联接点：{output}{error}");
    }

    private static OutputPlanItem Item(
        string path,
        string? title = null,
        IReadOnlyList<string>? artists = null,
        string? album = null,
        uint? disc = null,
        uint? track = null,
        string? groupOverride = null,
        int? manualOrder = null,
        string? albumIdentity = null) =>
        new(path, NcmAudioFormat.Mp3, title, artists ?? ["Artist"], album, disc, track, groupOverride, manualOrder, albumIdentity);

    private sealed class TemporaryDirectory : IDisposable
    {
        public string FullName { get; } = Directory.CreateTempSubdirectory().FullName;

        public void Dispose()
        {
            if (Directory.Exists(FullName))
            {
                Directory.Delete(FullName, recursive: true);
            }
        }
    }
}

