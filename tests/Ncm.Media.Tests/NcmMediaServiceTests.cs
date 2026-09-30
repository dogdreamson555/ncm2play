using System.Buffers.Binary;
using Ncm.Core;
using Ncm.Core.Tests;
using Ncm.Media;
using SkiaSharp;
using TagLib;

namespace Ncm.Media.Tests;

public sealed class NcmMediaServiceTests
{
    [Theory]
    [InlineData(NcmAudioFormat.Mp3, true, "Synthetic Track", "Synthetic Artist", "Synthetic Album")]
    [InlineData(NcmAudioFormat.Flac, true, "Synthetic Track", "Synthetic Artist", "Synthetic Album")]
    [InlineData(NcmAudioFormat.Mp3, false, "Input Track", "Input Artist", "Input Album")]
    [InlineData(NcmAudioFormat.Flac, false, "Input Track", "Input Artist", "Input Album")]
    public async Task ExportUsesNcmMetadataOrFallsBackToAudioTagsAndPreservesOtherTags(
        NcmAudioFormat format,
        bool hasNcmMetadata,
        string expectedTitle,
        string expectedArtist,
        string expectedAlbum)
    {
        using var workspace = new MediaTestWorkspace();
        var inputPath = await workspace.CreateNcmAsync(
            "tagged",
            format,
            hasNcmMetadata ? SyntheticMetadataMode.Valid : SyntheticMetadataMode.Missing,
            addAudioTags: true);

        var result = await new NcmMediaService().ExportAsync(inputPath, workspace.OutputDirectory);

        Assert.Equal(expectedTitle, result.Title);
        Assert.Equal(new[] { expectedArtist }, result.Artists);
        Assert.Equal(expectedAlbum, result.Album);
        using var output = TagLib.File.Create(result.OutputPath);
        Assert.Equal(expectedTitle, output.Tag.Title);
        Assert.Equal(new[] { expectedArtist }, output.Tag.Performers);
        Assert.Equal(expectedAlbum, output.Tag.Album);
        Assert.Equal("User comment", output.Tag.Comment);
        Assert.Equal(new[] { "Shoegaze" }, output.Tag.Genres);
        Assert.Equal((uint)2007, output.Tag.Year);
        Assert.Equal((uint)5, output.Tag.Track);
    }

    [Fact]
    public async Task NcmCoverTakesPriorityOverAudioPicture()
    {
        using var workspace = new MediaTestWorkspace();
        var ncmCover = CreateImage(24, 16, SKColors.Red);
        var audioCover = CreateImage(24, 16, SKColors.Blue);
        var inputPath = await workspace.CreateNcmAsync(
            "cover-priority",
            NcmAudioFormat.Mp3,
            ncmCover: ncmCover,
            audioCover: audioCover);

        var result = await new NcmMediaService().ExportAsync(inputPath, workspace.OutputDirectory);

        Assert.Equal(CoverOrigin.Ncm, result.CoverOrigin);
        using var output = TagLib.File.Create(result.OutputPath);
        Assert.Equal("image/png", Assert.Single(output.Tag.Pictures).MimeType);
        using var image = DecodeSinglePicture(output);
        Assert.Equal(SKColors.Red, image.GetPixel(0, 0));
    }

    [Fact]
    public async Task AudioPictureIsUsedWhenNcmCoverIsMissing()
    {
        using var workspace = new MediaTestWorkspace();
        var inputPath = await workspace.CreateNcmAsync(
            "audio-cover",
            NcmAudioFormat.Flac,
            metadataMode: SyntheticMetadataMode.Missing,
            addAudioTags: true,
            audioCover: CreateImage(32, 20, SKColors.Green));

        var result = await new NcmMediaService().ExportAsync(inputPath, workspace.OutputDirectory);

        Assert.Equal(CoverOrigin.Audio, result.CoverOrigin);
        using var output = TagLib.File.Create(result.OutputPath);
        using var image = DecodeSinglePicture(output);
        Assert.Equal(SKColors.Green, image.GetPixel(0, 0));
    }

    [Theory]
    [InlineData(CoverSelection.Original, CoverOrigin.Audio, true)]
    [InlineData(CoverSelection.Imported, CoverOrigin.Imported, true)]
    [InlineData(CoverSelection.None, CoverOrigin.None, false)]
    public async Task ApePictureIsSelectedOrRemovedWithoutLosingOtherApeFields(
        CoverSelection selection,
        CoverOrigin expectedOrigin,
        bool expectPicture)
    {
        using var workspace = new MediaTestWorkspace();
        var inputPath = await workspace.CreateNcmAsync(
            "ape-cover",
            NcmAudioFormat.Mp3,
            metadataMode: SyntheticMetadataMode.Missing,
            apeCover: CreateImage(22, 18, SKColors.Green));
        var importPath = workspace.WriteImport("import.png", CreateImage(22, 18, SKColors.Red));

        var result = await new NcmMediaService().ExportAsync(
            inputPath,
            workspace.OutputDirectory,
            new MediaExportOptions(selection, selection == CoverSelection.Imported ? importPath : null));

        Assert.Equal(expectedOrigin, result.CoverOrigin);
        using var output = TagLib.File.Create(result.OutputPath);
        Assert.Equal(expectPicture ? 1 : 0, output.Tag.Pictures.Length);
        if (expectPicture)
        {
            using var image = DecodeSinglePicture(output);
            Assert.Equal(selection == CoverSelection.Imported ? SKColors.Red : SKColors.Green, image.GetPixel(0, 0));
        }

        var ape = Assert.IsType<TagLib.Ape.Tag>(output.GetTag(TagTypes.Ape));
        Assert.Empty(ape.Pictures);
        Assert.Equal("Preserve this", ape.Comment);
    }

    [Fact]
    public async Task MissingOriginalCoverIsReported()
    {
        using var workspace = new MediaTestWorkspace();
        var inputPath = await workspace.CreateNcmAsync(
            "no-cover",
            NcmAudioFormat.Mp3,
            metadataMode: SyntheticMetadataMode.Missing);

        var result = await new NcmMediaService().ExportAsync(inputPath, workspace.OutputDirectory);

        Assert.Equal(CoverOrigin.None, result.CoverOrigin);
        Assert.True(result.OriginalCoverUnavailable);
        Assert.Equal("no-cover", result.Title);
        Assert.Equal(new[] { "未知歌手" }, result.Artists);
        Assert.Equal("未分类", result.Album);
        using var output = TagLib.File.Create(result.OutputPath);
        Assert.Empty(output.Tag.Pictures);
    }

    [Fact]
    public async Task CoverCapacityPreviewReportsMissingAndAudioFallback()
    {
        using var workspace = new MediaTestWorkspace();
        var missing = await workspace.CreateNcmAsync("preview-missing", NcmAudioFormat.Mp3, metadataMode: SyntheticMetadataMode.Missing);
        var withAudioCover = await workspace.CreateNcmAsync(
            "preview-audio",
            NcmAudioFormat.Flac,
            metadataMode: SyntheticMetadataMode.Missing,
            audioCover: CreateImage(24, 18, SKColors.Green));
        var service = new NcmMediaService();

        Assert.False(await service.ValidateCoverCapacityAsync(missing, new MediaExportOptions()));
        Assert.True(await service.ValidateCoverCapacityAsync(withAudioCover, new MediaExportOptions(AudioFormat: AudioExportFormat.Mp3)));
    }

    [Fact]
    public async Task CoverCapacityPreviewValidatesImportedImageForSelectedFormat()
    {
        using var workspace = new MediaTestWorkspace();
        var input = await workspace.CreateNcmAsync("preview-import", NcmAudioFormat.Mp3);
        var imagePath = workspace.WriteImport("preview.png", CreateImage(24, 18, SKColors.Green));
        var service = new NcmMediaService();

        Assert.True(await service.ValidateCoverCapacityAsync(
            input,
            new MediaExportOptions(CoverSelection.Imported, imagePath, AudioFormat: AudioExportFormat.Flac)));
        Assert.True(await service.ValidateCoverCapacityAsync(
            input,
            new MediaExportOptions(CoverSelection.Imported, imagePath, AudioFormat: AudioExportFormat.Mp3)));
    }

    [Fact]
    public async Task NoneSelectionRemovesNcmAndAudioPictures()
    {
        using var workspace = new MediaTestWorkspace();
        var inputPath = await workspace.CreateNcmAsync(
            "without-cover",
            NcmAudioFormat.Mp3,
            ncmCover: CreateImage(18, 18, SKColors.Red),
            addAudioTags: true,
            audioCover: CreateImage(18, 18, SKColors.Blue));

        var result = await new NcmMediaService().ExportAsync(
            inputPath,
            workspace.OutputDirectory,
            new MediaExportOptions(CoverSelection.None));

        Assert.Equal(CoverOrigin.None, result.CoverOrigin);
        using var output = TagLib.File.Create(result.OutputPath);
        Assert.Empty(output.Tag.Pictures);
    }

    [Theory]
    [InlineData(".jpg", SKEncodedImageFormat.Jpeg)]
    [InlineData(".png", SKEncodedImageFormat.Png)]
    public async Task ImportedLargeImagesKeepTheirOriginalDimensions(string extension, SKEncodedImageFormat format)
    {
        using var workspace = new MediaTestWorkspace();
        var inputPath = await workspace.CreateNcmAsync("imported-cover", NcmAudioFormat.Mp3);
        const int width = 820;
        const int height = 610;
        var importPath = workspace.WriteImport("cover" + extension, CreateImage(width, height, SKColors.Purple, format));

        var result = await new NcmMediaService().ExportAsync(
            inputPath,
            workspace.OutputDirectory,
            new MediaExportOptions(CoverSelection.Imported, importPath));

        Assert.Equal(CoverOrigin.Imported, result.CoverOrigin);
        using var output = TagLib.File.Create(result.OutputPath);
        Assert.Equal(format == SKEncodedImageFormat.Jpeg ? "image/jpeg" : "image/png", Assert.Single(output.Tag.Pictures).MimeType);
        using var image = DecodeSinglePicture(output);
        Assert.Equal(width, image.Width);
        Assert.Equal(height, image.Height);
    }

    [Theory]
    [InlineData(".jpg", SKEncodedImageFormat.Jpeg)]
    [InlineData(".png", SKEncodedImageFormat.Png)]
    public async Task ImportedJpegAndPngKeepTheirOriginalEncodedBytes(string extension, SKEncodedImageFormat format)
    {
        using var workspace = new MediaTestWorkspace();
        var inputPath = await workspace.CreateNcmAsync("raw-cover", NcmAudioFormat.Mp3);
        var encoded = CreateImage(38, 27, SKColors.Coral, format);
        var importPath = workspace.WriteImport("cover" + extension, encoded);

        var result = await new NcmMediaService().ExportAsync(
            inputPath,
            workspace.OutputDirectory,
            new MediaExportOptions(CoverSelection.Imported, importPath));

        using var output = TagLib.File.Create(result.OutputPath);
        Assert.Equal(encoded, Assert.Single(output.Tag.Pictures).Data.Data.ToArray());
    }

    [Fact]
    public async Task ImportedWebpConvertsToPngAndPreservesAlpha()
    {
        using var workspace = new MediaTestWorkspace();
        var inputPath = await workspace.CreateNcmAsync("webp-cover", NcmAudioFormat.Mp3);
        var encoded = CreateAlphaWebp();
        var importPath = workspace.WriteImport("cover.webp", encoded);

        var result = await new NcmMediaService().ExportAsync(
            inputPath,
            workspace.OutputDirectory,
            new MediaExportOptions(CoverSelection.Imported, importPath));

        using var output = TagLib.File.Create(result.OutputPath);
        var picture = Assert.Single(output.Tag.Pictures);
        Assert.Equal("image/png", picture.MimeType);
        using var image = SKBitmap.Decode(picture.Data.Data) ?? throw new InvalidDataException("Output picture could not be decoded.");
        Assert.Equal(0, image.GetPixel(2, 2).Alpha);
        Assert.InRange(image.GetPixel(20, 20).Alpha, (byte)120, (byte)136);
    }

    [Fact]
    public async Task ImportedBmpConvertsToPng()
    {
        using var workspace = new MediaTestWorkspace();
        var inputPath = await workspace.CreateNcmAsync("bmp-cover", NcmAudioFormat.Flac);
        var importPath = workspace.WriteImport("cover.bmp", CreateSolidBmp(41, 29, SKColors.DarkCyan));

        var result = await new NcmMediaService().ExportAsync(
            inputPath,
            workspace.OutputDirectory,
            new MediaExportOptions(CoverSelection.Imported, importPath));

        using var output = TagLib.File.Create(result.OutputPath);
        var picture = Assert.Single(output.Tag.Pictures);
        Assert.Equal("image/png", picture.MimeType);
        using var image = SKBitmap.Decode(picture.Data.Data) ?? throw new InvalidDataException("Output picture could not be decoded.");
        Assert.Equal(41, image.Width);
        Assert.Equal(29, image.Height);
        Assert.Equal(SKColors.DarkCyan, image.GetPixel(0, 0));
    }

    [Fact]
    public async Task ImportedIcoUsesLargestUsableImageAndConvertsToPng()
    {
        using var workspace = new MediaTestWorkspace();
        var inputPath = await workspace.CreateNcmAsync("ico-cover", NcmAudioFormat.Flac);
        var small = CreateImage(16, 16, SKColors.Red);
        var large = CreateImage(64, 48, SKColors.Blue);
        var importPath = workspace.WriteImport("cover.ico", CreateIco(small, large));

        var result = await new NcmMediaService().ExportAsync(
            inputPath,
            workspace.OutputDirectory,
            new MediaExportOptions(CoverSelection.Imported, importPath));

        using var output = TagLib.File.Create(result.OutputPath);
        var picture = Assert.Single(output.Tag.Pictures);
        Assert.Equal("image/png", picture.MimeType);
        using var image = SKBitmap.Decode(picture.Data.Data) ?? throw new InvalidDataException("Output picture could not be decoded.");
        Assert.Equal(64, image.Width);
        Assert.Equal(48, image.Height);
        Assert.Equal(SKColors.Blue, image.GetPixel(0, 0));
    }

    [Fact]
    public async Task ImportedIcoWithDibPayloadConvertsToPng()
    {
        using var workspace = new MediaTestWorkspace();
        var inputPath = await workspace.CreateNcmAsync("ico-dib-cover", NcmAudioFormat.Flac);
        var importPath = workspace.WriteImport("dib.ico", CreateIcoWithDib(SKColors.DarkOrange));

        var result = await new NcmMediaService().ExportAsync(
            inputPath,
            workspace.OutputDirectory,
            new MediaExportOptions(CoverSelection.Imported, importPath));

        using var output = TagLib.File.Create(result.OutputPath);
        var picture = Assert.Single(output.Tag.Pictures);
        Assert.Equal("image/png", picture.MimeType);
        using var image = SKBitmap.Decode(picture.Data.Data) ?? throw new InvalidDataException("Output picture could not be decoded.");
        Assert.Equal((2, 2), (image.Width, image.Height));
        Assert.Equal(SKColors.DarkOrange, image.GetPixel(0, 0));
    }

    [Fact]
    public async Task BmpLargerThanAudioTagLimitIsAcceptedWhenConvertedPngFits()
    {
        using var workspace = new MediaTestWorkspace();
        var inputPath = await workspace.CreateNcmAsync("large-bmp-cover", NcmAudioFormat.Flac);
        var bmp = CreateSolidBmp(2450, 2300, SKColors.SeaGreen);
        Assert.True(bmp.Length > MediaTagCapacity.MaximumFlacMetadataBlockLength);
        var importPath = workspace.WriteImport("large.bmp", bmp);
        var service = new NcmMediaService();
        var options = new MediaExportOptions(CoverSelection.Imported, importPath);

        Assert.True(await service.ValidateCoverCapacityAsync(inputPath, options));
        var result = await service.ExportAsync(inputPath, workspace.OutputDirectory, options);

        using var output = TagLib.File.Create(result.OutputPath);
        var picture = Assert.Single(output.Tag.Pictures);
        Assert.Equal("image/png", picture.MimeType);
        Assert.True(picture.Data.Count < MediaTagCapacity.MaximumFlacMetadataBlockLength);
        using var image = SKBitmap.Decode(picture.Data.Data) ?? throw new InvalidDataException("Output picture could not be decoded.");
        Assert.Equal(2450, image.Width);
        Assert.Equal(2300, image.Height);
    }

    [Fact]
    public void SupportedFileExtensionsAreReadOnlyAndIncludeAllImportFormats()
    {
        var extensions = CoverImageFormats.SupportedFileExtensions();
        Assert.Equal(
            new[] { ".jpg", ".jpeg", ".png", ".webp", ".avif", ".heic", ".heif", ".bmp", ".ico" },
            extensions);
        var mutableInterface = Assert.IsAssignableFrom<IList<string>>(extensions);
        Assert.Throws<NotSupportedException>(() => mutableInterface[0] = ".gif");
    }

    [Fact]
    public void HeicExtensionRejectsAnUnrelatedIsoContainerBrand()
    {
        var bytes = new byte[16];
        BinaryPrimitives.WriteUInt32BigEndian(bytes.AsSpan(0, 4), 16);
        "ftyp"u8.CopyTo(bytes.AsSpan(4, 4));
        "isom"u8.CopyTo(bytes.AsSpan(8, 4));

        Assert.Throws<MediaExportException>(() => CoverImageProcessor.Prepare(bytes, "misnamed.heic", null));
    }

    [Theory]
    [InlineData(900, 450, 500, 500, 250)]
    [InlineData(300, 150, 500, 300, 150)]
    [InlineData(900, 450, null, 900, 450)]
    public async Task OriginalCoverIsResizedProportionallyWithoutUpscaling(
        int sourceWidth,
        int sourceHeight,
        int? maximumEdge,
        int expectedWidth,
        int expectedHeight)
    {
        using var workspace = new MediaTestWorkspace();
        var inputPath = await workspace.CreateNcmAsync(
            "resized-cover",
            NcmAudioFormat.Flac,
            ncmCover: CreateImage(sourceWidth, sourceHeight, SKColors.Orange));

        var result = await new NcmMediaService().ExportAsync(
            inputPath,
            workspace.OutputDirectory,
            new MediaExportOptions(OriginalCoverMaximumEdge: maximumEdge));

        Assert.Equal(CoverOrigin.Ncm, result.CoverOrigin);
        using var output = TagLib.File.Create(result.OutputPath);
        using var image = DecodeSinglePicture(output);
        Assert.Equal(expectedWidth, image.Width);
        Assert.Equal(expectedHeight, image.Height);
    }

    [Theory]
    [InlineData(InvalidCoverKind.ExtensionMismatch)]
    [InlineData(InvalidCoverKind.CorruptImage)]
    [InlineData(InvalidCoverKind.FakeHugeDimensions)]
    public async Task InvalidImportedImagesAreRejectedWithoutCreatingOutput(InvalidCoverKind kind)
    {
        using var workspace = new MediaTestWorkspace();
        var inputPath = await workspace.CreateNcmAsync("invalid-import", NcmAudioFormat.Mp3);
        var (fileName, bytes) = kind switch
        {
            InvalidCoverKind.ExtensionMismatch => ("cover.jpg", CreateImage(12, 12, SKColors.Red)),
            InvalidCoverKind.CorruptImage => ("cover.png", CreateImage(12, 12, SKColors.Red)[..20]),
            InvalidCoverKind.FakeHugeDimensions => ("cover.png", CreatePngWithDimensions(50_000, 50_000)),
            _ => throw new ArgumentOutOfRangeException(nameof(kind))
        };
        var importPath = workspace.WriteImport(fileName, bytes);

        await Assert.ThrowsAsync<MediaExportException>(() => new NcmMediaService().ExportAsync(
            inputPath,
            workspace.OutputDirectory,
            new MediaExportOptions(CoverSelection.Imported, importPath)));

        var outputs = Directory.Exists(workspace.OutputDirectory)
            ? Directory.EnumerateFileSystemEntries(workspace.OutputDirectory)
            : Enumerable.Empty<string>();
        Assert.Empty(outputs);
    }

    [Fact]
    public async Task FailedExportDoesNotOverwriteAnExistingOutput()
    {
        using var workspace = new MediaTestWorkspace();
        var inputPath = await workspace.CreateNcmAsync("existing-output", NcmAudioFormat.Mp3);
        Directory.CreateDirectory(workspace.OutputDirectory);
        var existingOutput = Path.Combine(workspace.OutputDirectory, "existing-output.mp3");
        var sentinel = "existing user content"u8.ToArray();
        await System.IO.File.WriteAllBytesAsync(existingOutput, sentinel);
        var invalidImport = workspace.WriteImport("damaged.png", [0x89, 0x50, 0x4e, 0x47]);

        await Assert.ThrowsAnyAsync<IOException>(() => new NcmMediaService().ExportAsync(
            inputPath,
            workspace.OutputDirectory,
            new MediaExportOptions(CoverSelection.Imported, invalidImport)));

        Assert.Equal(sentinel, await System.IO.File.ReadAllBytesAsync(existingOutput));
        Assert.Equal(existingOutput, Assert.Single(Directory.EnumerateFiles(workspace.OutputDirectory)));
    }

    private static SKBitmap DecodeSinglePicture(TagLib.File media)
    {
        var picture = Assert.Single(media.Tag.Pictures);
        return SKBitmap.Decode(picture.Data.Data) ?? throw new InvalidDataException("Output picture could not be decoded.");
    }

    private static byte[] CreateImage(int width, int height, SKColor color, SKEncodedImageFormat format = SKEncodedImageFormat.Png)
    {
        using var bitmap = new SKBitmap(width, height);
        using (var canvas = new SKCanvas(bitmap))
        {
            canvas.Clear(color);
        }

        using var image = SKImage.FromBitmap(bitmap);
        using var encoded = image.Encode(format, 90);
        return encoded.ToArray();
    }

    private static byte[] CreateAlphaWebp()
    {
        using var bitmap = new SKBitmap(new SKImageInfo(32, 32, SKColorType.Rgba8888, SKAlphaType.Unpremul));
        using (var canvas = new SKCanvas(bitmap))
        {
            canvas.Clear(new SKColor(240, 20, 40, 0));
            canvas.DrawRect(new SKRect(12, 12, 32, 32), new SKPaint { Color = new SKColor(20, 220, 80, 128) });
        }

        using var image = SKImage.FromBitmap(bitmap);
        using var encoded = image.Encode(SKEncodedImageFormat.Webp, 100) ?? throw new InvalidOperationException("WebP encoding is unavailable.");
        return encoded.ToArray();
    }

    private static byte[] CreateIco(params byte[][] pngImages)
    {
        var directorySize = checked(6 + pngImages.Length * 16);
        var totalSize = checked(directorySize + pngImages.Sum(image => image.Length));
        var ico = new byte[totalSize];
        BinaryPrimitives.WriteUInt16LittleEndian(ico.AsSpan(2, 2), 1);
        BinaryPrimitives.WriteUInt16LittleEndian(ico.AsSpan(4, 2), checked((ushort)pngImages.Length));
        var imageOffset = directorySize;
        for (var index = 0; index < pngImages.Length; index++)
        {
            using var bitmap = SKBitmap.Decode(pngImages[index]) ?? throw new InvalidDataException("ICO source image could not be decoded.");
            var entry = ico.AsSpan(6 + index * 16, 16);
            entry[0] = bitmap.Width == 256 ? (byte)0 : checked((byte)bitmap.Width);
            entry[1] = bitmap.Height == 256 ? (byte)0 : checked((byte)bitmap.Height);
            BinaryPrimitives.WriteUInt16LittleEndian(entry[4..6], 1);
            BinaryPrimitives.WriteUInt16LittleEndian(entry[6..8], 32);
            BinaryPrimitives.WriteUInt32LittleEndian(entry[8..12], checked((uint)pngImages[index].Length));
            BinaryPrimitives.WriteUInt32LittleEndian(entry[12..16], checked((uint)imageOffset));
            pngImages[index].CopyTo(ico, imageOffset);
            imageOffset += pngImages[index].Length;
        }

        return ico;
    }

    private static byte[] CreateIcoWithDib(SKColor color)
    {
        const int directoryLength = 22;
        const int dibLength = 64;
        var ico = new byte[directoryLength + dibLength];
        BinaryPrimitives.WriteUInt16LittleEndian(ico.AsSpan(2, 2), 1);
        BinaryPrimitives.WriteUInt16LittleEndian(ico.AsSpan(4, 2), 1);
        ico[6] = 2;
        ico[7] = 2;
        BinaryPrimitives.WriteUInt16LittleEndian(ico.AsSpan(10, 2), 1);
        BinaryPrimitives.WriteUInt16LittleEndian(ico.AsSpan(12, 2), 32);
        BinaryPrimitives.WriteUInt32LittleEndian(ico.AsSpan(14, 4), dibLength);
        BinaryPrimitives.WriteUInt32LittleEndian(ico.AsSpan(18, 4), directoryLength);

        var dib = ico.AsSpan(directoryLength, dibLength);
        BinaryPrimitives.WriteUInt32LittleEndian(dib[..4], 40);
        BinaryPrimitives.WriteInt32LittleEndian(dib.Slice(4, 4), 2);
        BinaryPrimitives.WriteInt32LittleEndian(dib.Slice(8, 4), 4);
        BinaryPrimitives.WriteUInt16LittleEndian(dib.Slice(12, 2), 1);
        BinaryPrimitives.WriteUInt16LittleEndian(dib.Slice(14, 2), 32);
        BinaryPrimitives.WriteUInt32LittleEndian(dib.Slice(20, 4), 16);
        for (var index = 40; index < 56; index += 4)
        {
            dib[index] = color.Blue;
            dib[index + 1] = color.Green;
            dib[index + 2] = color.Red;
            dib[index + 3] = color.Alpha;
        }

        return ico;
    }

    private static byte[] CreateSolidBmp(int width, int height, SKColor color)
    {
        var rowBytes = checked((width * 3 + 3) & ~3);
        var pixelBytes = checked(rowBytes * height);
        var result = new byte[checked(54 + pixelBytes)];
        result[0] = (byte)'B';
        result[1] = (byte)'M';
        BinaryPrimitives.WriteUInt32LittleEndian(result.AsSpan(2, 4), checked((uint)result.Length));
        BinaryPrimitives.WriteUInt32LittleEndian(result.AsSpan(10, 4), 54);
        BinaryPrimitives.WriteUInt32LittleEndian(result.AsSpan(14, 4), 40);
        BinaryPrimitives.WriteInt32LittleEndian(result.AsSpan(18, 4), width);
        BinaryPrimitives.WriteInt32LittleEndian(result.AsSpan(22, 4), height);
        BinaryPrimitives.WriteUInt16LittleEndian(result.AsSpan(26, 2), 1);
        BinaryPrimitives.WriteUInt16LittleEndian(result.AsSpan(28, 2), 24);
        BinaryPrimitives.WriteUInt32LittleEndian(result.AsSpan(34, 4), checked((uint)pixelBytes));

        for (var y = 0; y < height; y++)
        {
            var row = result.AsSpan(54 + y * rowBytes, rowBytes);
            for (var x = 0; x < width; x++)
            {
                var pixel = row.Slice(x * 3, 3);
                pixel[0] = color.Blue;
                pixel[1] = color.Green;
                pixel[2] = color.Red;
            }
        }

        return result;
    }

    private static byte[] CreatePngWithDimensions(uint width, uint height)
    {
        var image = CreateImage(1, 1, SKColors.Black);
        BinaryPrimitives.WriteUInt32BigEndian(image.AsSpan(16, 4), width);
        BinaryPrimitives.WriteUInt32BigEndian(image.AsSpan(20, 4), height);
        BinaryPrimitives.WriteUInt32BigEndian(image.AsSpan(29, 4), PngCrc32(image.AsSpan(12, 17)));
        return image;
    }

    private static uint PngCrc32(ReadOnlySpan<byte> bytes)
    {
        var crc = uint.MaxValue;
        foreach (var value in bytes)
        {
            crc ^= value;
            for (var bit = 0; bit < 8; bit++)
            {
                crc = (crc & 1) != 0 ? (crc >> 1) ^ 0xedb88320 : crc >> 1;
            }
        }

        return ~crc;
    }

    public enum InvalidCoverKind
    {
        ExtensionMismatch,
        CorruptImage,
        FakeHugeDimensions
    }

    private sealed class MediaTestWorkspace : IDisposable
    {
        private readonly string _root = Directory.CreateTempSubdirectory().FullName;

        public string OutputDirectory => Path.Combine(_root, "output");

        public async Task<string> CreateNcmAsync(
            string name,
            NcmAudioFormat format,
            SyntheticMetadataMode metadataMode = SyntheticMetadataMode.Valid,
            byte[]? ncmCover = null,
            bool addAudioTags = false,
            byte[]? audioCover = null,
            byte[]? apeCover = null)
        {
            var audio = SyntheticAudio.Create(format);
            if (addAudioTags || audioCover is not null || apeCover is not null)
            {
                var extension = format == NcmAudioFormat.Mp3 ? ".mp3" : ".flac";
                var audioPath = Path.Combine(_root, "input-tags" + extension);
                await System.IO.File.WriteAllBytesAsync(audioPath, audio);
                using (var media = TagLib.File.Create(audioPath))
                {
                    if (addAudioTags)
                    {
                        media.Tag.Title = "Input Track";
                        media.Tag.Performers = ["Input Artist"];
                        media.Tag.Album = "Input Album";
                        media.Tag.Comment = "User comment";
                        media.Tag.Genres = ["Shoegaze"];
                        media.Tag.Year = 2007;
                        media.Tag.Track = 5;
                    }

                    if (audioCover is not null)
                    {
                        media.Tag.Pictures =
                        [
                            new Picture(new ByteVector(audioCover))
                            {
                                Type = PictureType.FrontCover,
                                MimeType = "image/png"
                            }
                        ];
                    }

                    if (apeCover is not null)
                    {
                        var ape = Assert.IsType<TagLib.Ape.Tag>(media.GetTag(TagTypes.Ape, true));
                        ape.Comment = "Preserve this";
                        ape.Pictures =
                        [
                            new Picture(new ByteVector(apeCover))
                            {
                                Type = PictureType.FrontCover,
                                MimeType = "image/png"
                            }
                        ];
                    }

                    media.Save();
                }

                audio = await System.IO.File.ReadAllBytesAsync(audioPath);
            }

            var inputPath = Path.Combine(_root, name + ".ncm");
            var fixture = SyntheticNcmFile.Create(
                format,
                metadataMode,
                cover: ncmCover,
                audioBytes: audio);
            await System.IO.File.WriteAllBytesAsync(inputPath, fixture.FileBytes);
            return inputPath;
        }

        public string WriteImport(string fileName, byte[] image)
        {
            var path = Path.Combine(_root, fileName);
            System.IO.File.WriteAllBytes(path, image);
            return path;
        }

        public void Dispose()
        {
            Directory.Delete(_root, recursive: true);
        }
    }
}
