using Ncm.Core;
using Ncm.Core.Tests;
using TagLib;

namespace Ncm.Media.Tests;

public sealed class NcmOutputPlanningTests
{
    [Fact]
    public async Task PlanningReadsSourceAudioTagsWhenNcmMetadataIsMissing()
    {
        using var workspace = new Workspace();
        var input = await workspace.CreateAsync("歌 02", 5, 2);
        var service = new NcmMediaService();

        var item = await service.InspectOutputItemAsync(input);

        Assert.Equal("Input Track", item.Title);
        Assert.Equal(new[] { "Input Artist" }, item.Artists);
        Assert.Equal("Input Album", item.Album);
        Assert.Equal((uint)5, item.TrackNumber);
        Assert.Equal((uint)2, item.DiscNumber);
        Assert.Equal(NcmAudioFormat.Mp3, item.OutputFormat);
    }

    [Fact]
    public async Task AlbumOrderSeparatesSameTitleAlbumsByNcmIdentity()
    {
        using var workspace = new Workspace();
        var first = await workspace.CreateAsync("first album", track: 1, albumId: 1001, albumArtist: "Same Artist");
        var second = await workspace.CreateAsync("second album", track: 1, albumId: 1002, albumArtist: "Same Artist");
        var service = new NcmMediaService();

        var plan = await service.PlanOutputsAsync(
            [first, second],
            workspace.OutputDirectory,
            new OutputPlanOptions(NumberTracks: true, TrackNumbering: OutputTrackNumbering.AlbumOrder));

        Assert.Equal(2, plan.Items.Count);
        Assert.Equal(plan.Items[0].Album, plan.Items[1].Album);
        Assert.NotEqual(plan.Items[0].Source.AlbumIdentity, plan.Items[1].Source.AlbumIdentity);
        Assert.Equal([1u, 1u], plan.Items.Select(static item => item.WrittenTrackNumber!.Value));
    }

    [Fact]
    public async Task AlbumArtistsKeepCompilationTracksInTheSameAlbum()
    {
        using var workspace = new Workspace();
        var first = await workspace.CreateAsync("performer one", track: 1, albumArtist: "Various Artists", performer: "One");
        var second = await workspace.CreateAsync("performer two", track: 1, albumArtist: "Various Artists", performer: "Two");
        var service = new NcmMediaService();

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => service.PlanOutputsAsync(
            [first, second],
            workspace.OutputDirectory,
            new OutputPlanOptions(NumberTracks: true, TrackNumbering: OutputTrackNumbering.AlbumOrder)));

        Assert.Contains("重复曲目号", error.Message, StringComparison.Ordinal);
        Assert.Contains("从上到下进行标号", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ManualAlbumOrderMatchesExportedFileNamesAndTags()
    {
        using var workspace = new Workspace();
        var first = await workspace.CreateAsync("曲 10", includeAlbum: false);
        var second = await workspace.CreateAsync("曲 2", includeAlbum: false);
        var service = new NcmMediaService();
        var original = await service.PlanOutputsAsync([first, second], workspace.OutputDirectory);
        var items = original.Items.Select((item, index) => item.Source with
        {
            GroupOverride = "手工专辑",
            ManualOrder = index == 0 ? 2 : 1
        }).ToArray();
        var plan = OutputPlanner.Create(
            workspace.OutputDirectory,
            items,
            new OutputPlanOptions(OutputClassification.Album, OutputFileNaming.SourceFileName, NumberTracks: true));

        var results = new List<MediaExportResult>();
        foreach (var item in plan.Items)
        {
            results.Add(await service.ExportPlannedAsync(plan, item));
        }

        Assert.Equal(2, results.Count);
        Assert.All(results, result => Assert.Equal("手工专辑", result.Album));
        foreach (var item in plan.Items)
        {
            var output = Assert.Single(results, result => Path.GetFileName(result.OutputPath) == item.FileName);
            Assert.StartsWith(item.WrittenTrackNumber!.Value.ToString("D2") + " - ", item.FileName);
            Assert.Equal("手工专辑", Path.GetFileName(Path.GetDirectoryName(output.OutputPath)));
            using var audio = TagLib.File.Create(output.OutputPath);
            Assert.Equal(item.WrittenTrackNumber.Value, audio.Tag.Track);
            Assert.Equal(item.WrittenDiscNumber!.Value, audio.Tag.Disc);
            Assert.Equal("手工专辑", audio.Tag.Album);
        }
    }

    [Fact]
    public async Task DefaultExportRenamesCollisionWithoutChangingExistingFile()
    {
        using var workspace = new Workspace();
        var input = await workspace.CreateAsync("重名");
        Directory.CreateDirectory(workspace.OutputDirectory);
        var existing = Path.Combine(workspace.OutputDirectory, "重名.mp3");
        var originalBytes = "existing content"u8.ToArray();
        await System.IO.File.WriteAllBytesAsync(existing, originalBytes);

        var result = await new NcmMediaService().ExportAsync(input, workspace.OutputDirectory);

        Assert.Equal("重名 (2).mp3", Path.GetFileName(result.OutputPath));
        Assert.Equal(originalBytes, await System.IO.File.ReadAllBytesAsync(existing));
        Assert.True(System.IO.File.Exists(result.OutputPath));
    }

    [Fact]
    public async Task MultiDiscPrefixesMatchExportedDiscAndTrackTags()
    {
        using var workspace = new Workspace();
        var secondDisc = await workspace.CreateAsync("disc2", track: 1, disc: 2);
        var firstDisc = await workspace.CreateAsync("disc1", track: 2, disc: 1);
        var service = new NcmMediaService();
        var plan = await service.PlanOutputsAsync(
            [secondDisc, firstDisc],
            workspace.OutputDirectory,
            new OutputPlanOptions(NumberTracks: true));

        Assert.Equal(["1-02 - disc1.mp3", "2-01 - disc2.mp3"], plan.Items.Select(item => item.FileName));
        foreach (var item in plan.Items)
        {
            var result = await service.ExportPlannedAsync(plan, item);
            using var audio = TagLib.File.Create(result.OutputPath);
            Assert.Equal(item.WrittenDiscNumber!.Value, audio.Tag.Disc);
            Assert.Equal(item.WrittenTrackNumber!.Value, audio.Tag.Track);
        }
    }

    private sealed class Workspace : IDisposable
    {
        private readonly string _root = Directory.CreateTempSubdirectory().FullName;

        public string OutputDirectory => Path.Combine(_root, "output");

        public async Task<string> CreateAsync(
            string name,
            uint track = 0,
            uint disc = 0,
            bool includeAlbum = true,
            long? albumId = null,
            string? albumArtist = null,
            string performer = "Input Artist")
        {
            var audioPath = Path.Combine(_root, name + ".mp3");
            await System.IO.File.WriteAllBytesAsync(audioPath, SyntheticAudio.Create(NcmAudioFormat.Mp3));
            using (var audio = TagLib.File.Create(audioPath))
            {
                audio.Tag.Title = "Input Track";
                audio.Tag.Performers = [performer];
                audio.Tag.AlbumArtists = albumArtist is null ? [] : [albumArtist];
                audio.Tag.Album = includeAlbum ? "Input Album" : null;
                audio.Tag.Track = track;
                audio.Tag.Disc = disc;
                audio.Save();
            }

            var fixture = SyntheticNcmFile.Create(
                NcmAudioFormat.Mp3,
                albumId.HasValue ? SyntheticMetadataMode.Valid : SyntheticMetadataMode.Missing,
                audioBytes: await System.IO.File.ReadAllBytesAsync(audioPath),
                albumId: albumId);
            var ncmPath = Path.Combine(_root, name + ".ncm");
            await System.IO.File.WriteAllBytesAsync(ncmPath, fixture.FileBytes);
            return ncmPath;
        }

        public void Dispose() => Directory.Delete(_root, recursive: true);
    }
}
