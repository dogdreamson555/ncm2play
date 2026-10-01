using Ncm.Core;
using TagLib;
using TagLib.Flac;
using TagLib.Id3v2;
using TagLib.Ogg;
using LocalFile = System.IO.File;
using TagFile = TagLib.File;

namespace Ncm.Media;

public sealed partial class NcmMediaService
{
    private const long MaximumImportedPictureLength = 128 * 1024 * 1024;
    private readonly NcmFileService _core;
    private readonly IAudioTranscoder _transcoder;

    public NcmMediaService(NcmFileService? core = null)
        : this(core ?? new NcmFileService(), new FfmpegAudioTranscoder())
    {
    }

    internal NcmMediaService(NcmFileService core, IAudioTranscoder transcoder)
    {
        _core = core;
        _transcoder = transcoder;
    }

    public async Task<MediaExportResult> ExportAsync(
        string inputPath,
        string outputDirectory,
        MediaExportOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        options ??= new MediaExportOptions();
        ValidateOptions(options);
        var plan = await PlanOutputsAsync([inputPath], outputDirectory, mediaOptions: options, cancellationToken: cancellationToken);
        return await ExportPlannedAsync(plan, plan.Items[0], options, cancellationToken);
    }

    private static void ValidateOptions(MediaExportOptions options)
    {
        if (options.CoverSelection == CoverSelection.Original &&
            options.OriginalCoverMaximumEdge is <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(options), "原封面最长边必须为正整数。");
        }

        if (options.CoverSelection == CoverSelection.Imported &&
            string.IsNullOrWhiteSpace(options.ImportedCoverPath))
        {
            throw new ArgumentException("导入封面时必须提供图片路径。", nameof(options));
        }

        _ = AudioTranscodePlanner.OutputFormat(NcmAudioFormat.Mp3, options);
        if (options.AudioFormat == AudioExportFormat.Mp3 &&
            options.Mp3BitrateKbps is not (128 or 192 or 256 or 320))
        {
            throw new ArgumentOutOfRangeException(nameof(options), "MP3 固定码率仅支持 128、192、256 或 320 kbps。");
        }

        if (options.AudioFormat == AudioExportFormat.Flac &&
            options.FlacCompressionLevel is < 0 or > 8)
        {
            throw new ArgumentOutOfRangeException(nameof(options), "FLAC 压缩级别仅支持 0 到 8。");
        }
    }

    internal static TagFile OpenTagFile(string path, NcmAudioFormat format) =>
        format switch
        {
            NcmAudioFormat.Mp3 => new TagLib.Mpeg.AudioFile(path, ReadStyle.None),
            NcmAudioFormat.Flac => new TagLib.Flac.File(path, ReadStyle.None),
            _ => throw new UnsupportedFormatException()
        };

    public async Task<AudioExportPreview> PreviewAsync(
        string inputPath,
        MediaExportOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        options ??= new MediaExportOptions();
        _ = AudioTranscodePlanner.OutputFormat(NcmAudioFormat.Mp3, options);
        var stageDirectory = Path.Combine(Path.GetTempPath(), $"ncm-preview-{Guid.NewGuid():N}");
        string? sourcePath = null;
        try
        {
            var source = await _core.ExportAsync(inputPath, stageDirectory, cancellationToken);
            sourcePath = source.OutputPath;
            var info = await _transcoder.ProbeAsync(sourcePath, cancellationToken);
            if (info.CodecFormat != source.Inspection.Format)
            {
                throw new MediaExportException("源音频格式与 NCM 内容识别结果不一致。");
            }

            return AudioTranscodePlanner.Create(source.Inspection.Format, info, options).Preview;
        }
        finally
        {
            if (sourcePath is not null && LocalFile.Exists(sourcePath))
            {
                LocalFile.Delete(sourcePath);
            }

            if (Directory.Exists(stageDirectory))
            {
                Directory.Delete(stageDirectory);
            }
        }
    }

    private static TaggingOutcome ApplyTags(
        string audioPath,
        string sourceAudioPath,
        NcmInspection inspection,
        NcmAudioFormat outputFormat,
        MediaExportOptions options,
        CancellationToken cancellationToken,
        PlannedOutputItem? plannedItem = null)
    {
        cancellationToken.ThrowIfCancellationRequested();
        try
        {
            using var source = OpenTagFile(sourceAudioPath, inspection.Format);
            using var target = sourceAudioPath == audioPath
                ? null
                : OpenTagFile(audioPath, outputFormat);
            var file = target ?? source;
            var title = plannedItem?.Title ?? FirstText(
                inspection.Metadata?.Title,
                source.Tag.Title,
                Path.GetFileNameWithoutExtension(inspection.SourcePath))!;
            var artists = plannedItem?.Artists.ToArray() ?? SelectArtists(inspection.Metadata?.Artists, source.Tag.Performers);
            var album = plannedItem?.Album ?? FirstText(inspection.Metadata?.Album, source.Tag.Album, "未分类")!;
            var sourcePicture = options.CoverSelection == CoverSelection.Original &&
                inspection.CoverData is null
                ? GetExistingPicture(source, inspection.Format)
                : null;

            if (target is not null)
            {
                source.Tag.CopyTo(target.Tag, overwrite: true);
            }

            CoverImage? cover = null;
            var origin = CoverOrigin.None;
            var unavailable = false;
            if (options.CoverSelection == CoverSelection.Imported)
            {
                var picturePath = Path.GetFullPath(options.ImportedCoverPath!);
                var bytes = ReadImportedPicture(picturePath, cancellationToken);
                cover = CoverImageProcessor.Prepare(bytes, picturePath, null, cancellationToken);
                origin = CoverOrigin.Imported;
            }
            else if (options.CoverSelection == CoverSelection.Original)
            {
                var bytes = inspection.CoverData ?? sourcePicture?.Data;
                if (bytes is null)
                {
                    unavailable = true;
                }
                else
                {
                    cover = CoverImageProcessor.Prepare(
                        bytes,
                        null,
                        options.OriginalCoverMaximumEdge,
                        cancellationToken);
                    origin = inspection.CoverData is null ? CoverOrigin.Audio : CoverOrigin.Ncm;
                }
            }

            cancellationToken.ThrowIfCancellationRequested();
            if (outputFormat == NcmAudioFormat.Mp3)
            {
                WriteMp3Tags(file, title, artists, album, cover, plannedItem?.WrittenTrackNumber, plannedItem?.WrittenDiscNumber);
            }
            else
            {
                WriteFlacTags(file, title, artists, album, cover, plannedItem?.WrittenTrackNumber, plannedItem?.WrittenDiscNumber);
            }

            cancellationToken.ThrowIfCancellationRequested();
            file.Save();
            return new TaggingOutcome(title, artists, album, origin, unavailable);
        }
        catch (MediaExportException)
        {
            throw;
        }
        catch (OutOfMemoryException exception)
        {
            throw new MediaExportException("音频标签或封面超出可用内存，请更换或调整封面。", exception);
        }
        catch (Exception exception) when (exception is CorruptFileException or UnsupportedFormatException or IOException or UnauthorizedAccessException or ArgumentException)
        {
            throw new MediaExportException("读取或写入音频标签失败。", exception);
        }
    }

    private static void WriteMp3Tags(
        TagFile file,
        string title,
        string[] artists,
        string album,
        CoverImage? cover,
        uint? trackNumber = null,
        uint? discNumber = null)
    {
        if (file.GetTag(TagTypes.Id3v2, true) is not TagLib.Id3v2.Tag id3)
        {
            throw new MediaExportException("无法创建 MP3 ID3v2 标签。");
        }

        id3.Title = title;
        id3.Performers = artists;
        id3.Album = album;
        if (trackNumber is { } track)
        {
            id3.Track = track;
            id3.Disc = discNumber ?? 1;
        }
        foreach (var frame in id3.GetFrames<AttachmentFrame>(new ByteVector("APIC"u8.ToArray())).ToArray())
        {
            id3.RemoveFrame(frame);
        }

        if (file.GetTag(TagTypes.Ape) is TagLib.Ape.Tag ape)
        {
            ape.Pictures = [];
        }

        if (cover is not null)
        {
            var picture = NewPicture(cover);
            id3.AddFrame(new AttachmentFrame(picture) { TextEncoding = StringType.Latin1 });
        }

        var rendered = id3.Render();
        var hasFooter = id3.Version == 4 && (id3.Flags & HeaderFlags.FooterPresent) != 0;
        MediaTagCapacity.CheckId3TagBody(rendered.Count, hasFooter);
    }

    private static void WriteFlacTags(
        TagFile file,
        string title,
        string[] artists,
        string album,
        CoverImage? cover,
        uint? trackNumber = null,
        uint? discNumber = null)
    {
        if (file.GetTag(TagTypes.Xiph, true) is not XiphComment xiph ||
            file.GetTag(TagTypes.FlacMetadata, true) is not Metadata metadata)
        {
            throw new MediaExportException("无法创建 FLAC 标签。");
        }

        xiph.Title = title;
        xiph.Performers = artists;
        xiph.Album = album;
        if (trackNumber is { } track)
        {
            xiph.Track = track;
            xiph.Disc = discNumber ?? 1;
        }
        xiph.RemoveField("COVERART");
        xiph.RemoveField("COVERARTMIME");
        xiph.RemoveField("METADATA_BLOCK_PICTURE");

        if (cover is null)
        {
            metadata.Pictures = [];
        }
        else
        {
            var picture = new TagLib.Flac.Picture(NewPicture(cover))
            {
                Width = cover.Width,
                Height = cover.Height,
                ColorDepth = cover.ColorDepth,
                IndexedColors = 0
            };
            MediaTagCapacity.CheckFlacPictureBlock(
                cover.Data.LongLength,
                System.Text.Encoding.ASCII.GetByteCount(cover.MimeType),
                0);
            if (picture.Render().Count > MediaTagCapacity.MaximumFlacMetadataBlockLength)
            {
                throw new MediaExportException("FLAC PICTURE 块超过 24 位长度上限，请更换或调整封面。");
            }

            metadata.Pictures = [picture];
        }

        if (xiph.Render(false).Count > MediaTagCapacity.MaximumFlacMetadataBlockLength)
        {
            throw new MediaExportException("FLAC 文本标签超过 24 位长度上限。");
        }
    }

    private static TagLib.Picture NewPicture(CoverImage cover) => new(new ByteVector(cover.Data))
    {
        Type = PictureType.FrontCover,
        MimeType = cover.MimeType,
        Description = string.Empty
    };

    private static ExistingPicture? GetExistingPicture(TagFile file, NcmAudioFormat format)
    {
        IPicture[] pictures;
        if (format == NcmAudioFormat.Mp3)
        {
            var id3Pictures = file.GetTag(TagTypes.Id3v2) is TagLib.Id3v2.Tag id3
                ? id3.GetFrames<AttachmentFrame>(new ByteVector("APIC"u8.ToArray())).Cast<IPicture>().ToArray()
                : [];
            pictures = id3Pictures.Concat(file.GetTag(TagTypes.Ape) is TagLib.Ape.Tag ape
                ? ape.Pictures
                : []).ToArray();
        }
        else
        {
            var native = file.GetTag(TagTypes.FlacMetadata) is Metadata metadata
                ? metadata.Pictures
                : [];
            pictures = native.Length > 0
                ? native
                : file.GetTag(TagTypes.Xiph) is XiphComment xiph ? xiph.Pictures : [];
        }

        var selected = pictures.FirstOrDefault(picture => picture.Type == PictureType.FrontCover) ??
            pictures.FirstOrDefault();
        return selected is null ? null : new ExistingPicture((byte[])selected.Data.Data.Clone());
    }

    private static byte[] ReadImportedPicture(
        string path,
        CancellationToken cancellationToken)
    {
        try
        {
            using var input = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            if (input.Length <= 0 || input.Length > MaximumImportedPictureLength)
            {
                throw new MediaExportException("导入封面文件为空或超过输入资源限制。请更换图片。");
            }

            var bytes = new byte[(int)input.Length];
            var position = 0;
            while (position < bytes.Length)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var count = input.Read(bytes, position, Math.Min(65536, bytes.Length - position));
                if (count == 0)
                {
                    throw new EndOfStreamException("封面文件读取时被截断。");
                }

                position += count;
            }

            return bytes;
        }
        catch (MediaExportException)
        {
            throw;
        }
        catch (OutOfMemoryException exception)
        {
            throw new MediaExportException("导入封面超出可用内存，请更换图片。", exception);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            throw new MediaExportException("无法读取导入的封面文件。", exception);
        }
    }

    private static string[] SelectArtists(IReadOnlyList<string>? ncmArtists, string[]? audioArtists)
    {
        var selected = ncmArtists?.Where(value => !string.IsNullOrWhiteSpace(value)).ToArray();
        if (selected is { Length: > 0 })
        {
            return selected;
        }

        selected = audioArtists?.Where(value => !string.IsNullOrWhiteSpace(value)).ToArray();
        return selected is { Length: > 0 } ? selected : ["未知歌手"];
    }

    private static string? FirstText(params string?[] values) =>
        values.FirstOrDefault(value => !string.IsNullOrWhiteSpace(value));

    private sealed record ExistingPicture(byte[] Data);

    private sealed record TaggingOutcome(
        string Title,
        string[] Artists,
        string Album,
        CoverOrigin CoverOrigin,
        bool OriginalCoverUnavailable);
}
