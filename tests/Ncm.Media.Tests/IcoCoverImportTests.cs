using System.Buffers.Binary;
using Ncm.Core;
using Ncm.Core.Tests;
using Ncm.Media;
using SkiaSharp;

namespace Ncm.Media.Tests;

public sealed class IcoCoverImportTests
{
    [Fact]
    public async Task Imported24BitDibAppliesAndMaskTransparency()
    {
        var xor = new byte[] { 0, 0, 255, 0, 255, 0, 0, 0 };
        var and = new byte[] { 0x80, 0, 0, 0 };

        using var image = SKBitmap.Decode(await ExportImportedIconAsync(CreateIconWithDib(2, 1, 24, xor, and)))
            ?? throw new InvalidDataException("Exported cover could not be decoded.");

        Assert.Equal((byte)0, image.GetPixel(0, 0).Alpha);
        Assert.Equal(new SKColor(0, 255, 0), image.GetPixel(1, 0));
    }

    [Fact]
    public async Task ImportedLowBitDepthDibAppliesAndMaskTransparency()
    {
        var palette = new byte[] { 0, 0, 0, 0, 255, 255, 255, 0 };
        var xor = new byte[] { 0x40, 0, 0, 0 };
        var and = new byte[] { 0x80, 0, 0, 0 };

        using var image = SKBitmap.Decode(await ExportImportedIconAsync(CreateIconWithDib(2, 1, 1, xor, and, palette)))
            ?? throw new InvalidDataException("Exported cover could not be decoded.");

        Assert.Equal((byte)0, image.GetPixel(0, 0).Alpha);
        Assert.Equal(SKColors.White, image.GetPixel(1, 0));
    }

    [Fact]
    public async Task Imported32BitDibUsesAndMaskWhenAlphaBytesAreAllZero()
    {
        var xor = new byte[] { 0, 0, 255, 0, 0, 255, 0, 0 };
        var and = new byte[] { 0x80, 0, 0, 0 };

        using var image = SKBitmap.Decode(await ExportImportedIconAsync(CreateIconWithDib(2, 1, 32, xor, and)))
            ?? throw new InvalidDataException("Exported cover could not be decoded.");

        Assert.Equal((byte)0, image.GetPixel(0, 0).Alpha);
        Assert.Equal(byte.MaxValue, image.GetPixel(1, 0).Alpha);
    }

    [Fact]
    public async Task Imported32BitDibUsesPerPixelAlphaWhenPresent()
    {
        var xor = new byte[] { 0, 0, 255, 128, 0, 255, 0, 0 };
        var and = new byte[] { 0x80, 0, 0, 0 };

        using var image = SKBitmap.Decode(await ExportImportedIconAsync(CreateIconWithDib(2, 1, 32, xor, and)))
            ?? throw new InvalidDataException("Exported cover could not be decoded.");

        Assert.Equal((byte)128, image.GetPixel(0, 0).Alpha);
        Assert.Equal((byte)0, image.GetPixel(1, 0).Alpha);
    }

    private static async Task<byte[]> ExportImportedIconAsync(byte[] icon)
    {
        using var workspace = new IcoTestWorkspace();
        var inputPath = await workspace.CreateNcmAsync();
        var importPath = workspace.WriteImport(icon);
        var result = await new NcmMediaService().ExportAsync(
            inputPath,
            workspace.OutputDirectory,
            new MediaExportOptions(CoverSelection.Imported, importPath));

        Assert.Equal(CoverOrigin.Imported, result.CoverOrigin);
        using var output = TagLib.File.Create(result.OutputPath);
        var picture = Assert.Single(output.Tag.Pictures);
        Assert.Equal("image/png", picture.MimeType);
        return picture.Data.Data.ToArray();
    }

    private static byte[] CreateIconWithDib(
        int width,
        int height,
        ushort bitCount,
        byte[] xor,
        byte[] and,
        byte[]? palette = null)
    {
        palette ??= [];
        var dibLength = checked(40 + palette.Length + xor.Length + and.Length);
        const int directoryLength = 22;
        var icon = new byte[checked(directoryLength + dibLength)];
        BinaryPrimitives.WriteUInt16LittleEndian(icon.AsSpan(2, 2), 1);
        BinaryPrimitives.WriteUInt16LittleEndian(icon.AsSpan(4, 2), 1);
        icon[6] = checked((byte)width);
        icon[7] = checked((byte)height);
        BinaryPrimitives.WriteUInt16LittleEndian(icon.AsSpan(10, 2), 1);
        BinaryPrimitives.WriteUInt16LittleEndian(icon.AsSpan(12, 2), bitCount);
        BinaryPrimitives.WriteUInt32LittleEndian(icon.AsSpan(14, 4), checked((uint)dibLength));
        BinaryPrimitives.WriteUInt32LittleEndian(icon.AsSpan(18, 4), directoryLength);

        var dib = icon.AsSpan(directoryLength, dibLength);
        BinaryPrimitives.WriteUInt32LittleEndian(dib[..4], 40);
        BinaryPrimitives.WriteInt32LittleEndian(dib.Slice(4, 4), width);
        BinaryPrimitives.WriteInt32LittleEndian(dib.Slice(8, 4), checked(height * 2));
        BinaryPrimitives.WriteUInt16LittleEndian(dib.Slice(12, 2), 1);
        BinaryPrimitives.WriteUInt16LittleEndian(dib.Slice(14, 2), bitCount);
        BinaryPrimitives.WriteUInt32LittleEndian(dib.Slice(20, 4), checked((uint)xor.Length));
        if (bitCount <= 8)
        {
            BinaryPrimitives.WriteUInt32LittleEndian(dib.Slice(32, 4), checked((uint)(palette.Length / 4)));
        }

        var offset = 40;
        palette.AsSpan().CopyTo(dib[offset..]);
        offset += palette.Length;
        xor.AsSpan().CopyTo(dib[offset..]);
        offset += xor.Length;
        and.AsSpan().CopyTo(dib[offset..]);
        return icon;
    }

    private sealed class IcoTestWorkspace : IDisposable
    {
        private readonly string _root = Directory.CreateTempSubdirectory().FullName;

        public string OutputDirectory => Path.Combine(_root, "output");

        public async Task<string> CreateNcmAsync()
        {
            var fixture = SyntheticNcmFile.Create(NcmAudioFormat.Flac);
            var path = Path.Combine(_root, "input.ncm");
            await File.WriteAllBytesAsync(path, fixture.FileBytes);
            return path;
        }

        public string WriteImport(byte[] icon)
        {
            var path = Path.Combine(_root, "cover.ico");
            File.WriteAllBytes(path, icon);
            return path;
        }

        public void Dispose() => Directory.Delete(_root, recursive: true);
    }
}
