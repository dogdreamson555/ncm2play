using System.Buffers.Binary;
using SkiaSharp;

namespace Ncm.Media.Tests;

public sealed class CoverImageOrientationTests
{
    [Theory]
    [InlineData(SKEncodedOrigin.TopLeft)]
    [InlineData(SKEncodedOrigin.TopRight)]
    [InlineData(SKEncodedOrigin.BottomRight)]
    [InlineData(SKEncodedOrigin.BottomLeft)]
    [InlineData(SKEncodedOrigin.LeftTop)]
    [InlineData(SKEncodedOrigin.RightTop)]
    [InlineData(SKEncodedOrigin.RightBottom)]
    [InlineData(SKEncodedOrigin.LeftBottom)]
    public void ResizedJpegAppliesExifOrientation(SKEncodedOrigin origin)
    {
        var encoded = AddExifOrientation(CreateAsymmetricJpeg(), origin);
        using var sourceStream = new MemoryStream(encoded, writable: false);
        using var sourceCodec = SKCodec.Create(sourceStream, out var createResult)
            ?? throw new InvalidDataException("Test JPEG could not be decoded.");
        Assert.Equal(SKCodecResult.Success, createResult);
        Assert.Equal(origin, sourceCodec.EncodedOrigin);

        var cover = CoverImageProcessor.Prepare(encoded, "cover.jpg", 40);
        var swapsDimensions = origin is SKEncodedOrigin.LeftTop or SKEncodedOrigin.RightTop or
            SKEncodedOrigin.RightBottom or SKEncodedOrigin.LeftBottom;
        Assert.Equal(swapsDimensions ? 27 : 40, cover.Width);
        Assert.Equal(swapsDimensions ? 40 : 27, cover.Height);

        using var output = SKBitmap.Decode(cover.Data) ?? throw new InvalidDataException("Output cover could not be decoded.");
        Assert.Equal(cover.Width, output.Width);
        Assert.Equal(cover.Height, output.Height);

        var expectedCorners = GetExpectedCorners(origin);
        AssertPixelNear(expectedCorners.TopLeft, output.GetPixel(2, 2));
        AssertPixelNear(expectedCorners.TopRight, output.GetPixel(output.Width - 3, 2));
        AssertPixelNear(expectedCorners.BottomLeft, output.GetPixel(2, output.Height - 3));
        AssertPixelNear(expectedCorners.BottomRight, output.GetPixel(output.Width - 3, output.Height - 3));
    }

    [Fact]
    public void UnscaledJpegRetainsOriginalExifBytes()
    {
        var encoded = AddExifOrientation(CreateAsymmetricJpeg(), SKEncodedOrigin.RightTop);

        var cover = CoverImageProcessor.Prepare(encoded, "cover.jpg", null);

        Assert.Same(encoded, cover.Data);
        Assert.Equal(120, cover.Width);
        Assert.Equal(80, cover.Height);
    }

    [Fact]
    public void ReencodedWebpRotationPreservesAlpha()
    {
        var encoded = AddWebpExifOrientation(CreateAlphaWebp(), SKEncodedOrigin.RightTop, 120, 80);
        using var sourceStream = new MemoryStream(encoded, writable: false);
        using var sourceCodec = SKCodec.Create(sourceStream, out var createResult)
            ?? throw new InvalidDataException("Test WebP could not be decoded.");
        Assert.Equal(SKCodecResult.Success, createResult);
        Assert.Equal(SKEncodedOrigin.RightTop, sourceCodec.EncodedOrigin);
        Assert.NotEqual(SKAlphaType.Opaque, sourceCodec.Info.AlphaType);

        var cover = CoverImageProcessor.Prepare(encoded, "cover.webp", null);

        Assert.Equal("image/png", cover.MimeType);
        Assert.Equal(80, cover.Width);
        Assert.Equal(120, cover.Height);
        using var output = SKBitmap.Decode(cover.Data) ?? throw new InvalidDataException("Output cover could not be decoded.");
        Assert.Equal(80, output.Width);
        Assert.Equal(120, output.Height);
        Assert.Equal((byte)255, output.GetPixel(2, 2).Alpha);
        Assert.Equal((byte)255, output.GetPixel(output.Width - 3, 2).Alpha);
        Assert.InRange(output.GetPixel(2, output.Height - 3).Alpha, (byte)126, (byte)130);
        Assert.Equal((byte)0, output.GetPixel(output.Width - 3, output.Height - 3).Alpha);

        AssertPixelNear(SKColors.Blue, output.GetPixel(2, 2));
        AssertPixelNear(SKColors.Red, output.GetPixel(output.Width - 3, 2));
        AssertPixelNear(SKColors.Yellow, output.GetPixel(2, output.Height - 3));
    }

    private static byte[] CreateAsymmetricJpeg()
    {
        const int width = 120;
        const int height = 80;
        using var bitmap = new SKBitmap(new SKImageInfo(width, height, SKColorType.Rgba8888, SKAlphaType.Opaque));
        using (var canvas = new SKCanvas(bitmap))
        using (var paint = new SKPaint())
        {
            canvas.Clear(SKColors.Black);
            paint.Color = SKColors.Red;
            canvas.DrawRect(new SKRect(0, 0, width / 2, height / 2), paint);
            paint.Color = SKColors.Green;
            canvas.DrawRect(new SKRect(width / 2, 0, width, height / 2), paint);
            paint.Color = SKColors.Blue;
            canvas.DrawRect(new SKRect(0, height / 2, width / 2, height), paint);
            paint.Color = SKColors.Yellow;
            canvas.DrawRect(new SKRect(width / 2, height / 2, width, height), paint);
            canvas.Flush();
        }

        using var image = SKImage.FromBitmap(bitmap);
        using var encoded = image.Encode(SKEncodedImageFormat.Jpeg, 100);
        return encoded.ToArray();
    }

    private static byte[] AddExifOrientation(byte[] jpeg, SKEncodedOrigin origin)
    {
        var tiff = CreateExifTiff(origin);
        var payload = new byte[tiff.Length + 6];
        "Exif\0\0"u8.CopyTo(payload);
        tiff.AsSpan().CopyTo(payload.AsSpan(6));

        var segmentLength = checked((ushort)(payload.Length + 2));
        var result = new byte[jpeg.Length + payload.Length + 4];
        jpeg.AsSpan(0, 2).CopyTo(result);
        result[2] = 0xff;
        result[3] = 0xe1;
        BinaryPrimitives.WriteUInt16BigEndian(result.AsSpan(4, 2), segmentLength);
        payload.AsSpan().CopyTo(result.AsSpan(6));
        jpeg.AsSpan(2).CopyTo(result.AsSpan(6 + payload.Length));
        return result;
    }

    private static byte[] CreateExifTiff(SKEncodedOrigin origin)
    {
        var tiff = new byte[26];
        tiff[0] = (byte)'I';
        tiff[1] = (byte)'I';
        BinaryPrimitives.WriteUInt16LittleEndian(tiff.AsSpan(2, 2), 42);
        BinaryPrimitives.WriteUInt32LittleEndian(tiff.AsSpan(4, 4), 8);
        BinaryPrimitives.WriteUInt16LittleEndian(tiff.AsSpan(8, 2), 1);
        BinaryPrimitives.WriteUInt16LittleEndian(tiff.AsSpan(10, 2), 0x0112);
        BinaryPrimitives.WriteUInt16LittleEndian(tiff.AsSpan(12, 2), 3);
        BinaryPrimitives.WriteUInt32LittleEndian(tiff.AsSpan(14, 4), 1);
        BinaryPrimitives.WriteUInt16LittleEndian(tiff.AsSpan(18, 2), (ushort)origin);
        return tiff;
    }

    private static byte[] CreateAlphaWebp()
    {
        const int width = 120;
        const int height = 80;
        using var bitmap = new SKBitmap(new SKImageInfo(width, height, SKColorType.Rgba8888, SKAlphaType.Premul));
        using (var canvas = new SKCanvas(bitmap))
        using (var paint = new SKPaint())
        {
            canvas.Clear(SKColors.Transparent);
            paint.Color = SKColors.Red;
            canvas.DrawRect(new SKRect(0, 0, width / 2, height / 2), paint);
            paint.Color = SKColors.Blue;
            canvas.DrawRect(new SKRect(0, height / 2, width / 2, height), paint);
            paint.Color = new SKColor(255, 255, 0, 128);
            canvas.DrawRect(new SKRect(width / 2, height / 2, width, height), paint);
            canvas.Flush();
        }

        using var image = SKImage.FromBitmap(bitmap);
        using var encoded = image.Encode(SKEncodedImageFormat.Webp, 100);
        return encoded.ToArray();
    }

    private static byte[] AddWebpExifOrientation(
        byte[] webp,
        SKEncodedOrigin origin,
        int width,
        int height)
    {
        if (webp.Length < 12 || !webp.AsSpan(0, 4).SequenceEqual("RIFF"u8) ||
            !webp.AsSpan(8, 4).SequenceEqual("WEBP"u8))
        {
            throw new InvalidDataException("Encoded test image is not a WebP RIFF container.");
        }

        var chunks = new List<byte[]>();
        var offset = 12;
        var extendedHeader = -1;
        while (offset < webp.Length)
        {
            if (webp.Length - offset < 8)
            {
                throw new InvalidDataException("Encoded WebP contains a truncated chunk header.");
            }

            var payloadLength = BinaryPrimitives.ReadUInt32LittleEndian(webp.AsSpan(offset + 4, 4));
            var chunkLength = checked(8 + (int)payloadLength + (int)(payloadLength & 1));
            if (chunkLength > webp.Length - offset)
            {
                throw new InvalidDataException("Encoded WebP contains a truncated chunk.");
            }

            var chunk = webp.AsSpan(offset, chunkLength).ToArray();
            if (chunk.AsSpan(0, 4).SequenceEqual("VP8X"u8))
            {
                extendedHeader = chunks.Count;
            }

            chunks.Add(chunk);
            offset += chunkLength;
        }

        if (extendedHeader >= 0)
        {
            chunks[extendedHeader][8] |= 0x18;
        }
        else
        {
            var header = new byte[18];
            "VP8X"u8.CopyTo(header);
            BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(4, 4), 10);
            header[8] = 0x18;
            WriteUInt24LittleEndian(header.AsSpan(12, 3), (uint)(width - 1));
            WriteUInt24LittleEndian(header.AsSpan(15, 3), (uint)(height - 1));
            chunks.Insert(0, header);
        }

        var exif = CreateExifTiff(origin);
        var exifChunk = new byte[8 + exif.Length + (exif.Length & 1)];
        "EXIF"u8.CopyTo(exifChunk);
        BinaryPrimitives.WriteUInt32LittleEndian(exifChunk.AsSpan(4, 4), (uint)exif.Length);
        exif.AsSpan().CopyTo(exifChunk.AsSpan(8));
        chunks.Add(exifChunk);

        var totalLength = 12 + chunks.Sum(chunk => chunk.Length);
        var result = new byte[totalLength];
        "RIFF"u8.CopyTo(result);
        BinaryPrimitives.WriteUInt32LittleEndian(result.AsSpan(4, 4), (uint)(totalLength - 8));
        "WEBP"u8.CopyTo(result.AsSpan(8));
        offset = 12;
        foreach (var chunk in chunks)
        {
            chunk.AsSpan().CopyTo(result.AsSpan(offset));
            offset += chunk.Length;
        }

        return result;
    }

    private static void WriteUInt24LittleEndian(Span<byte> destination, uint value)
    {
        destination[0] = (byte)value;
        destination[1] = (byte)(value >> 8);
        destination[2] = (byte)(value >> 16);
    }

    private static (SKColor TopLeft, SKColor TopRight, SKColor BottomLeft, SKColor BottomRight) GetExpectedCorners(
        SKEncodedOrigin origin) => origin switch
    {
        SKEncodedOrigin.TopLeft => (SKColors.Red, SKColors.Green, SKColors.Blue, SKColors.Yellow),
        SKEncodedOrigin.TopRight => (SKColors.Green, SKColors.Red, SKColors.Yellow, SKColors.Blue),
        SKEncodedOrigin.BottomRight => (SKColors.Yellow, SKColors.Blue, SKColors.Green, SKColors.Red),
        SKEncodedOrigin.BottomLeft => (SKColors.Blue, SKColors.Yellow, SKColors.Red, SKColors.Green),
        SKEncodedOrigin.LeftTop => (SKColors.Red, SKColors.Blue, SKColors.Green, SKColors.Yellow),
        SKEncodedOrigin.RightTop => (SKColors.Blue, SKColors.Red, SKColors.Yellow, SKColors.Green),
        SKEncodedOrigin.RightBottom => (SKColors.Yellow, SKColors.Green, SKColors.Blue, SKColors.Red),
        SKEncodedOrigin.LeftBottom => (SKColors.Green, SKColors.Yellow, SKColors.Red, SKColors.Blue),
        _ => throw new ArgumentOutOfRangeException(nameof(origin))
    };

    private static void AssertPixelNear(SKColor expected, SKColor actual)
    {
        var distance = Math.Abs(expected.Red - actual.Red) +
            Math.Abs(expected.Green - actual.Green) +
            Math.Abs(expected.Blue - actual.Blue);
        Assert.True(distance < 100, $"Expected color near {expected}, received {actual}.");
    }
}
