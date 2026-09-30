using System.Buffers.Binary;
using SkiaSharp;

namespace Ncm.Media;

internal sealed record CoverImage(byte[] Data, string MimeType, int Width, int Height, int ColorDepth);

public static class CoverImageFormats
{
    private static readonly IReadOnlyList<string> FileExtensions = Array.AsReadOnly(
        new[] { ".jpg", ".jpeg", ".png", ".webp", ".avif", ".heic", ".heif", ".bmp", ".ico" });

    public static IReadOnlyList<string> SupportedFileExtensions() => FileExtensions;
}

internal static class CoverImageProcessor
{
    private const long MaximumPixelCount = 32_000_000;
    private const int MaximumIconCount = 256;

    private readonly record struct IconDibLayout(
        byte[] BitmapFile,
        int Width,
        int Height,
        int BitCount,
        int XorOffset,
        int XorStride,
        int AndOffset,
        int AndStride,
        bool IsTopDown,
        int AlphaByteOffset,
        uint AlphaMask,
        int AlphaShift,
        uint AlphaValueMask);

    public static CoverImage Prepare(
        byte[] data,
        string? sourceFileName,
        int? maximumEdge,
        CancellationToken cancellationToken = default)
    {
        if (data is null || data.Length == 0)
        {
            throw new MediaExportException("封面图片为空。");
        }

        if (maximumEdge is <= 0)
        {
            throw new MediaExportException("封面最长边必须大于零。");
        }

        cancellationToken.ThrowIfCancellationRequested();
        var extension = sourceFileName is null ? null : Path.GetExtension(sourceFileName).ToLowerInvariant();
        if (extension is not null && !CoverImageFormats.SupportedFileExtensions().Contains(extension, StringComparer.Ordinal))
        {
            throw new MediaExportException("封面文件扩展名不受支持。");
        }

        try
        {
            if (extension is ".heic" or ".heif" or ".avif" || extension is null && LooksLikeIsoStillImage(data))
            {
                using var isoBitmap = FfmpegStillImageDecoder.Decode(data, extension, cancellationToken);
                return EncodeBitmap(isoBitmap, maximumEdge, cancellationToken);
            }

            if (extension == ".ico")
            {
                using var iconBitmap = DecodeIcon(data, cancellationToken);
                return EncodeBitmap(iconBitmap, maximumEdge, cancellationToken);
            }

            using var stream = new MemoryStream(data, writable: false);
            using var codec = SKCodec.Create(stream, out var createResult);
            if (codec is null || createResult != SKCodecResult.Success)
            {
                throw new MediaExportException("封面不是有效且完整的图片。");
            }

            var encodedFormat = codec.EncodedFormat;
            var outputFormat = encodedFormat switch
            {
                SKEncodedImageFormat.Jpeg => SKEncodedImageFormat.Jpeg,
                SKEncodedImageFormat.Png => SKEncodedImageFormat.Png,
                SKEncodedImageFormat.Webp or SKEncodedImageFormat.Bmp => SKEncodedImageFormat.Png,
                _ => throw new MediaExportException("封面只支持 JPEG、PNG、WebP、HEIC、AVIF、BMP 和 ICO 图片。")
            };
            ValidateExtension(extension, encodedFormat);

            var sourceInfo = codec.Info;
            ValidateDimensions(sourceInfo.Width, sourceInfo.Height, sourceInfo.BitsPerPixel);
            using var bitmap = new SKBitmap(sourceInfo);
            if (!bitmap.ReadyToDraw || codec.GetPixels(bitmap.Info, bitmap.GetPixels()) != SKCodecResult.Success)
            {
                throw new MediaExportException("封面图片损坏或数据不完整。");
            }

            var (orientedWidth, orientedHeight) = GetOrientedSize(sourceInfo.Width, sourceInfo.Height, codec.EncodedOrigin);
            var needsResize = maximumEdge is { } edge && Math.Max(orientedWidth, orientedHeight) > edge;
            if (!needsResize && outputFormat == encodedFormat)
            {
                return new CoverImage(data, GetMimeType(encodedFormat), sourceInfo.Width, sourceInfo.Height, sourceInfo.BitsPerPixel);
            }

            using var oriented = ApplyEncodedOrigin(bitmap, codec.EncodedOrigin);
            var orientedBitmap = oriented ?? bitmap;
            if (!needsResize)
            {
                return EncodeBitmap(orientedBitmap, outputFormat, cancellationToken);
            }

            cancellationToken.ThrowIfCancellationRequested();
            var (targetWidth, targetHeight) = GetTargetSize(orientedBitmap.Width, orientedBitmap.Height, maximumEdge!.Value);
            var targetInfo = new SKImageInfo(
                targetWidth,
                targetHeight,
                orientedBitmap.Info.ColorType,
                orientedBitmap.Info.AlphaType,
                orientedBitmap.Info.ColorSpace);
            using var resized = orientedBitmap.Resize(targetInfo, new SKSamplingOptions(SKFilterMode.Linear));
            if (resized is null || !resized.ReadyToDraw)
            {
                throw new MediaExportException("封面缩放失败。");
            }

            return EncodeBitmap(resized, outputFormat, cancellationToken);
        }
        catch (MediaExportException)
        {
            throw;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception)
        {
            throw new MediaExportException("无法读取或处理封面图片。", exception);
        }
    }

    private static CoverImage EncodeBitmap(
        SKBitmap bitmap,
        int? maximumEdge,
        CancellationToken cancellationToken)
    {
        var format = SKEncodedImageFormat.Png;
        if (maximumEdge is not { } edge || Math.Max(bitmap.Width, bitmap.Height) <= edge)
        {
            return EncodeBitmap(bitmap, format, cancellationToken);
        }

        var (width, height) = GetTargetSize(bitmap.Width, bitmap.Height, edge);
        var targetInfo = new SKImageInfo(width, height, bitmap.Info.ColorType, bitmap.Info.AlphaType, bitmap.Info.ColorSpace);
        using var resized = bitmap.Resize(targetInfo, new SKSamplingOptions(SKFilterMode.Linear));
        if (resized is null || !resized.ReadyToDraw)
        {
            throw new MediaExportException("封面缩放失败。");
        }

        return EncodeBitmap(resized, format, cancellationToken);
    }

    private static SKBitmap? ApplyEncodedOrigin(SKBitmap bitmap, SKEncodedOrigin origin)
    {
        if (origin == SKEncodedOrigin.TopLeft)
        {
            return null;
        }

        var (width, height) = GetOrientedSize(bitmap.Width, bitmap.Height, origin);
        var matrix = SKMatrix.CreateIdentity();
        switch (origin)
        {
            case SKEncodedOrigin.TopRight:
                matrix.ScaleX = -1;
                matrix.TransX = width;
                break;
            case SKEncodedOrigin.BottomRight:
                matrix.ScaleX = -1;
                matrix.TransX = width;
                matrix.ScaleY = -1;
                matrix.TransY = height;
                break;
            case SKEncodedOrigin.BottomLeft:
                matrix.ScaleY = -1;
                matrix.TransY = height;
                break;
            case SKEncodedOrigin.LeftTop:
                matrix.ScaleX = 0;
                matrix.SkewX = 1;
                matrix.SkewY = 1;
                matrix.ScaleY = 0;
                break;
            case SKEncodedOrigin.RightTop:
                matrix.ScaleX = 0;
                matrix.SkewX = -1;
                matrix.TransX = width;
                matrix.SkewY = 1;
                matrix.ScaleY = 0;
                break;
            case SKEncodedOrigin.RightBottom:
                matrix.ScaleX = 0;
                matrix.SkewX = -1;
                matrix.TransX = width;
                matrix.SkewY = -1;
                matrix.ScaleY = 0;
                matrix.TransY = height;
                break;
            case SKEncodedOrigin.LeftBottom:
                matrix.ScaleX = 0;
                matrix.SkewX = 1;
                matrix.SkewY = -1;
                matrix.ScaleY = 0;
                matrix.TransY = height;
                break;
            default:
                throw new MediaExportException("封面图片方向无效。");
        }

        var oriented = new SKBitmap(new SKImageInfo(
            width,
            height,
            bitmap.Info.ColorType,
            bitmap.Info.AlphaType,
            bitmap.Info.ColorSpace));
        try
        {
            if (!oriented.ReadyToDraw)
            {
                throw new MediaExportException("封面图片方向无法处理。");
            }

            using var canvas = new SKCanvas(oriented);
            canvas.Clear(SKColors.Transparent);
            canvas.SetMatrix(matrix);
            canvas.DrawBitmap(bitmap, 0, 0, new SKSamplingOptions(SKFilterMode.Nearest));
            canvas.Flush();
            return oriented;
        }
        catch
        {
            oriented.Dispose();
            throw;
        }
    }

    private static (int Width, int Height) GetOrientedSize(int width, int height, SKEncodedOrigin origin) => origin switch
    {
        SKEncodedOrigin.TopLeft or SKEncodedOrigin.TopRight or
        SKEncodedOrigin.BottomRight or SKEncodedOrigin.BottomLeft => (width, height),
        SKEncodedOrigin.LeftTop or SKEncodedOrigin.RightTop or
        SKEncodedOrigin.RightBottom or SKEncodedOrigin.LeftBottom => (height, width),
        _ => throw new MediaExportException("封面图片方向无效。")
    };

    private static CoverImage EncodeBitmap(SKBitmap bitmap, SKEncodedImageFormat format, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ValidateDimensions(bitmap.Width, bitmap.Height, bitmap.Info.BitsPerPixel);
        using var image = SKImage.FromBitmap(bitmap);
        using var encoded = image.Encode(format, format == SKEncodedImageFormat.Jpeg ? 90 : 100);
        if (encoded is null || encoded.Size == 0)
        {
            throw new MediaExportException("封面编码失败。");
        }

        cancellationToken.ThrowIfCancellationRequested();
        return new CoverImage(
            encoded.ToArray(),
            GetMimeType(format),
            bitmap.Width,
            bitmap.Height,
            bitmap.Info.BitsPerPixel);
    }

    private static SKBitmap DecodeIcon(byte[] data, CancellationToken cancellationToken)
    {
        if (data.Length < 6 || BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(0, 2)) != 0 ||
            BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(2, 2)) != 1)
        {
            throw new MediaExportException("ICO 封面文件头无效。");
        }

        var count = BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(4, 2));
        if (count is 0 or > MaximumIconCount || 6L + count * 16L > data.Length)
        {
            throw new MediaExportException("ICO 封面图像列表无效或超过资源限制。");
        }

        SKBitmap? largest = null;
        MediaExportException? lastError = null;
        try
        {
            for (var index = 0; index < count; index++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var entry = data.AsSpan(6 + index * 16, 16);
                var payloadLength = BinaryPrimitives.ReadUInt32LittleEndian(entry[8..12]);
                var payloadOffset = BinaryPrimitives.ReadUInt32LittleEndian(entry[12..16]);
                if (payloadLength == 0 || payloadOffset < 6 + count * 16 ||
                    (ulong)payloadOffset + payloadLength > (ulong)data.Length)
                {
                    continue;
                }

                SKBitmap? candidate = null;
                try
                {
                    candidate = DecodeIconPayload(data, (int)payloadOffset, (int)payloadLength, cancellationToken);
                }
                catch (Exception exception) when (exception is not OperationCanceledException and not OutOfMemoryException)
                {
                    lastError = exception as MediaExportException ??
                        new MediaExportException("ICO 图像项无法解码。", exception);
                }

                if (candidate is null)
                {
                    continue;
                }

                if (largest is null || (long)candidate.Width * candidate.Height > (long)largest.Width * largest.Height)
                {
                    largest?.Dispose();
                    largest = candidate;
                }
                else
                {
                    candidate.Dispose();
                }
            }
        }
        catch
        {
            largest?.Dispose();
            throw;
        }

        if (largest is not null)
        {
            return largest;
        }

        throw lastError is null
            ? new MediaExportException("ICO 封面不包含可解码的图像。")
            : new MediaExportException("ICO 封面不包含可解码的图像。", lastError);
    }

    private static SKBitmap DecodeIconPayload(
        byte[] source,
        int payloadOffset,
        int payloadLength,
        CancellationToken cancellationToken)
    {
        var payload = source.AsSpan(payloadOffset, payloadLength);
        if (payload.Length >= 8 && payload[0] == 0x89 && payload[1] == (byte)'P' &&
            payload[2] == (byte)'N' && payload[3] == (byte)'G' && payload[4] == 0x0d &&
            payload[5] == 0x0a && payload[6] == 0x1a && payload[7] == 0x0a)
        {
            using var pngStream = new MemoryStream(payload.ToArray(), writable: false);
            return DecodeSkiaBitmap(pngStream, cancellationToken);
        }

        var dibLayout = CreateBmpFromIconDib(payload);
        SKBitmap dibBitmap;
        using (var dibStream = new MemoryStream(dibLayout.BitmapFile, writable: false))
        {
            dibBitmap = DecodeSkiaBitmap(dibStream, cancellationToken);
        }

        using (dibBitmap)
        {
            dibLayout = dibLayout with { BitmapFile = [] };
            return ApplyIconTransparency(dibBitmap, payload, dibLayout, cancellationToken);
        }
    }

    private static IconDibLayout CreateBmpFromIconDib(ReadOnlySpan<byte> dib)
    {
        if (dib.Length < 12)
        {
            throw new MediaExportException("ICO 图像数据不完整。");
        }

        var headerSizeValue = BinaryPrimitives.ReadUInt32LittleEndian(dib[..4]);
        if (headerSizeValue > dib.Length || headerSizeValue < 12)
        {
            throw new MediaExportException("ICO DIB 图像头无效。");
        }

        var headerSize = checked((int)headerSizeValue);
        int heightOffset;
        int width;
        long originalHeight;
        int bitCount;
        uint compression = 0;
        uint imageSize = 0;
        uint colorsUsed = 0;
        long paletteEntrySize;
        var isCoreHeader = headerSize == 12;
        if (isCoreHeader)
        {
            width = BinaryPrimitives.ReadUInt16LittleEndian(dib.Slice(4, 2));
            heightOffset = 6;
            originalHeight = BinaryPrimitives.ReadUInt16LittleEndian(dib.Slice(heightOffset, 2));
            bitCount = BinaryPrimitives.ReadUInt16LittleEndian(dib.Slice(10, 2));
            paletteEntrySize = 3;
            if (BinaryPrimitives.ReadUInt16LittleEndian(dib.Slice(8, 2)) != 1)
            {
                throw new MediaExportException("ICO DIB 图像平面数无效。");
            }
        }
        else if (headerSize >= 40)
        {
            width = BinaryPrimitives.ReadInt32LittleEndian(dib.Slice(4, 4));
            heightOffset = 8;
            originalHeight = BinaryPrimitives.ReadInt32LittleEndian(dib.Slice(heightOffset, 4));
            bitCount = BinaryPrimitives.ReadUInt16LittleEndian(dib.Slice(14, 2));
            compression = BinaryPrimitives.ReadUInt32LittleEndian(dib.Slice(16, 4));
            imageSize = BinaryPrimitives.ReadUInt32LittleEndian(dib.Slice(20, 4));
            colorsUsed = BinaryPrimitives.ReadUInt32LittleEndian(dib.Slice(32, 4));
            paletteEntrySize = 4;
            if (BinaryPrimitives.ReadUInt16LittleEndian(dib.Slice(12, 2)) != 1)
            {
                throw new MediaExportException("ICO DIB 图像平面数无效。");
            }
        }
        else
        {
            throw new MediaExportException("ICO DIB 图像格式不受支持。");
        }

        if (width <= 0 || originalHeight == 0 || originalHeight == long.MinValue ||
            Math.Abs(originalHeight) % 2 != 0 || bitCount is not (1 or 4 or 8 or 16 or 24 or 32))
        {
            throw new MediaExportException("ICO DIB 图像尺寸无效。");
        }

        if (isCoreHeader && bitCount is not (1 or 4 or 8 or 24))
        {
            throw new MediaExportException("ICO DIB 图像格式不受支持。");
        }

        if (compression is not (0 or 1 or 2 or 3 or 6) ||
            compression == 1 && bitCount != 8 || compression == 2 && bitCount != 4 ||
            (compression is 3 or 6) && bitCount is not (16 or 32) || compression == 6 && bitCount != 32)
        {
            throw new MediaExportException("ICO DIB 压缩格式不受支持。");
        }

        var imageHeight = checked((int)(Math.Abs(originalHeight) / 2));
        ValidateDimensions(width, imageHeight, bitCount);
        var topDown = !isCoreHeader && originalHeight < 0;

        var paletteEntries = colorsUsed != 0 ? colorsUsed : bitCount <= 8 ? 1u << bitCount : 0;
        var usesBitFields = compression is 3 or 6;
        var externalMasksLength = usesBitFields && headerSize == 40 ? compression == 3 ? 12 : 16 : 0;
        uint alphaMask = 0;
        int alphaShift = 0;
        uint alphaValueMask = 0;
        if (usesBitFields)
        {
            if (headerSize != 40 && headerSize < (compression == 6 ? 56 : 52) ||
                (long)headerSize + externalMasksLength > dib.Length)
            {
                throw new MediaExportException("ICO DIB 颜色掩码无效。");
            }

            var masksOffset = 40;
            var redMask = BinaryPrimitives.ReadUInt32LittleEndian(dib.Slice(masksOffset, 4));
            var greenMask = BinaryPrimitives.ReadUInt32LittleEndian(dib.Slice(masksOffset + 4, 4));
            var blueMask = BinaryPrimitives.ReadUInt32LittleEndian(dib.Slice(masksOffset + 8, 4));
            alphaMask = compression == 6
                ? BinaryPrimitives.ReadUInt32LittleEndian(dib.Slice(masksOffset + 12, 4))
                : headerSize >= 56
                    ? BinaryPrimitives.ReadUInt32LittleEndian(dib.Slice(masksOffset + 12, 4))
                    : 0;
            var availableBits = bitCount == 32 ? uint.MaxValue : (1u << bitCount) - 1;
            if (redMask == 0 || greenMask == 0 || blueMask == 0 ||
                (redMask | greenMask | blueMask | alphaMask) > availableBits ||
                (redMask & greenMask) != 0 || (redMask & blueMask) != 0 || (greenMask & blueMask) != 0 ||
                (alphaMask & (redMask | greenMask | blueMask)) != 0 || compression == 6 && alphaMask == 0)
            {
                throw new MediaExportException("ICO DIB 颜色掩码无效。");
            }

            if (bitCount == 32 && alphaMask != 0)
            {
                alphaShift = GetMaskShiftAndValueMask(alphaMask, out alphaValueMask);
            }
        }

        var xorOffset = checked((long)headerSize + externalMasksLength + paletteEntries * paletteEntrySize);
        var xorStride = checked(((long)width * bitCount + 31) / 32 * 4);
        var xorLength = compression is 1 or 2 ? imageSize : checked(xorStride * imageHeight);
        if ((compression is 1 or 2) && (imageSize == 0 || topDown))
        {
            throw new MediaExportException("ICO DIB 压缩图像数据无效。");
        }
        var andStride = checked(((long)width + 31) / 32 * 4);
        var andLength = checked(andStride * imageHeight);
        var andOffset = checked(xorOffset + xorLength);
        var requiredLength = checked(andOffset + andLength);
        if (xorOffset > int.MaxValue || xorStride > int.MaxValue || andOffset > int.MaxValue ||
            andStride > int.MaxValue || requiredLength > dib.Length)
        {
            throw new MediaExportException("ICO DIB 图像数据不完整或超过资源限制。");
        }

        var alphaByteOffset = bitCount == 32 && compression == 0 ? 3 : -1;

        var pixelOffset = checked(14L + xorOffset);
        var fileLength = checked(14L + dib.Length);
        if (pixelOffset > fileLength || pixelOffset > uint.MaxValue || fileLength > int.MaxValue)
        {
            throw new MediaExportException("ICO DIB 图像数据无效。");
        }

        var bmp = new byte[(int)fileLength];
        bmp[0] = (byte)'B';
        bmp[1] = (byte)'M';
        BinaryPrimitives.WriteUInt32LittleEndian(bmp.AsSpan(2, 4), (uint)fileLength);
        BinaryPrimitives.WriteUInt32LittleEndian(bmp.AsSpan(10, 4), (uint)pixelOffset);
        dib.CopyTo(bmp.AsSpan(14));
        var mutableDib = bmp.AsSpan(14);
        if (isCoreHeader)
        {
            BinaryPrimitives.WriteUInt16LittleEndian(mutableDib.Slice(heightOffset, 2), checked((ushort)imageHeight));
        }
        else
        {
            BinaryPrimitives.WriteInt32LittleEndian(mutableDib.Slice(heightOffset, 4), topDown ? -imageHeight : imageHeight);
        }

        return new IconDibLayout(
            bmp,
            width,
            imageHeight,
            bitCount,
            checked((int)xorOffset),
            checked((int)xorStride),
            checked((int)andOffset),
            checked((int)andStride),
            topDown,
            alphaByteOffset,
            alphaMask,
            alphaShift,
            alphaValueMask);
    }

    private static SKBitmap ApplyIconTransparency(
        SKBitmap source,
        ReadOnlySpan<byte> dib,
        IconDibLayout layout,
        CancellationToken cancellationToken)
    {
        var hasPerPixelAlpha = HasPerPixelAlpha(dib, layout, cancellationToken);
        var info = new SKImageInfo(layout.Width, layout.Height, SKColorType.Rgba8888, SKAlphaType.Unpremul);
        var bitmap = new SKBitmap(info);
        try
        {
            if (!bitmap.ReadyToDraw)
            {
                throw new MediaExportException("ICO DIB 图像无法转换为 RGBA。");
            }

            using (var canvas = new SKCanvas(bitmap))
            {
                canvas.Clear(SKColors.Transparent);
                canvas.DrawBitmap(source, 0, 0, new SKSamplingOptions(SKFilterMode.Nearest));
                canvas.Flush();
            }

            unsafe
            {
                var pixels = (byte*)bitmap.GetPixels().ToPointer();
                if (pixels == null)
                {
                    throw new MediaExportException("ICO DIB 图像像素数据无效。");
                }

                for (var y = 0; y < layout.Height; y++)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var sourceY = layout.IsTopDown ? y : layout.Height - 1 - y;
                    var andRow = dib.Slice(layout.AndOffset + sourceY * layout.AndStride, layout.AndStride);
                    var xorRow = hasPerPixelAlpha
                        ? dib.Slice(layout.XorOffset + sourceY * layout.XorStride, layout.XorStride)
                        : default;
                    var outputRow = pixels + y * bitmap.RowBytes;
                    for (var x = 0; x < layout.Width; x++)
                    {
                        var transparentByMask = (andRow[x >> 3] & (0x80 >> (x & 7))) != 0;
                        var alpha = hasPerPixelAlpha
                            ? ReadPerPixelAlpha(xorRow.Slice(x * 4, 4), layout)
                            : transparentByMask ? (byte)0 : (byte)255;
                        outputRow[x * 4 + 3] = alpha;
                    }
                }
            }

            return bitmap;
        }
        catch
        {
            bitmap.Dispose();
            throw;
        }
    }

    private static bool HasPerPixelAlpha(
        ReadOnlySpan<byte> dib,
        IconDibLayout layout,
        CancellationToken cancellationToken)
    {
        if (layout.BitCount != 32 || layout.AlphaByteOffset < 0 && layout.AlphaMask == 0)
        {
            return false;
        }

        for (var y = 0; y < layout.Height; y++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var sourceY = layout.IsTopDown ? y : layout.Height - 1 - y;
            var row = dib.Slice(layout.XorOffset + sourceY * layout.XorStride, layout.XorStride);
            for (var x = 0; x < layout.Width; x++)
            {
                if (ReadPerPixelAlpha(row.Slice(x * 4, 4), layout) != 0)
                {
                    return true;
                }
            }
        }

        return false;
    }

    private static byte ReadPerPixelAlpha(ReadOnlySpan<byte> pixel, IconDibLayout layout)
    {
        if (layout.AlphaByteOffset >= 0)
        {
            return pixel[layout.AlphaByteOffset];
        }

        if (layout.AlphaMask == 0)
        {
            return 0;
        }

        var value = BinaryPrimitives.ReadUInt32LittleEndian(pixel) & layout.AlphaMask;
        var component = value >> layout.AlphaShift;
        return (byte)(((ulong)component * byte.MaxValue + layout.AlphaValueMask / 2u) / layout.AlphaValueMask);
    }

    private static int GetMaskShiftAndValueMask(uint mask, out uint valueMask)
    {
        var shift = 0;
        while ((mask & 1) == 0)
        {
            mask >>= 1;
            shift++;
        }

        if (mask != uint.MaxValue && (mask & (mask + 1)) != 0)
        {
            throw new MediaExportException("ICO DIB Alpha 掩码无效。");
        }

        valueMask = mask;
        return shift;
    }

    private static SKBitmap DecodeSkiaBitmap(Stream stream, CancellationToken cancellationToken)
    {
        using var codec = SKCodec.Create(stream, out var createResult);
        if (codec is null || createResult != SKCodecResult.Success)
        {
            throw new MediaExportException("ICO 图像数据损坏或格式不受支持。");
        }

        ValidateDimensions(codec.Info.Width, codec.Info.Height, codec.Info.BitsPerPixel);
        var bitmap = new SKBitmap(codec.Info);
        try
        {
            if (!bitmap.ReadyToDraw || codec.GetPixels(bitmap.Info, bitmap.GetPixels()) != SKCodecResult.Success)
            {
                throw new MediaExportException("ICO 图像数据损坏或不完整。");
            }

            cancellationToken.ThrowIfCancellationRequested();
            return bitmap;
        }
        catch
        {
            bitmap.Dispose();
            throw;
        }
    }

    private static bool LooksLikeIsoStillImage(ReadOnlySpan<byte> data)
    {
        if (data.Length < 12 || !data.Slice(4, 4).SequenceEqual("ftyp"u8))
        {
            return false;
        }

        var brand = data.Slice(8, 4);
        return brand.SequenceEqual("heic"u8) || brand.SequenceEqual("heix"u8) ||
            brand.SequenceEqual("hevc"u8) || brand.SequenceEqual("hevx"u8) ||
            brand.SequenceEqual("mif1"u8) || brand.SequenceEqual("msf1"u8) ||
            brand.SequenceEqual("avif"u8) || brand.SequenceEqual("avis"u8);
    }

    private static void ValidateExtension(string? extension, SKEncodedImageFormat format)
    {
        if (extension is null)
        {
            return;
        }

        var expectedFormat = extension switch
        {
            ".jpg" or ".jpeg" => SKEncodedImageFormat.Jpeg,
            ".png" => SKEncodedImageFormat.Png,
            ".webp" => SKEncodedImageFormat.Webp,
            ".bmp" => SKEncodedImageFormat.Bmp,
            _ => throw new MediaExportException("封面文件扩展名与图片内容不匹配。")
        };

        if (expectedFormat != format)
        {
            throw new MediaExportException("封面文件扩展名与图片内容不匹配。");
        }
    }

    private static void ValidateDimensions(int width, int height, int bitsPerPixel)
    {
        if (width <= 0 || height <= 0 || (long)width * height > MaximumPixelCount || bitsPerPixel <= 0)
        {
            throw new MediaExportException("封面尺寸无效或超过解码资源限制。");
        }
    }

    private static string GetMimeType(SKEncodedImageFormat format) => format switch
    {
        SKEncodedImageFormat.Jpeg => "image/jpeg",
        SKEncodedImageFormat.Png => "image/png",
        _ => throw new MediaExportException("封面编码格式不受支持。")
    };

    private static (int Width, int Height) GetTargetSize(int width, int height, int maximumEdge)
    {
        if (width >= height)
        {
            var targetHeight = Math.Max(1, (int)Math.Round((double)height * maximumEdge / width));
            return (maximumEdge, targetHeight);
        }

        var targetWidth = Math.Max(1, (int)Math.Round((double)width * maximumEdge / height));
        return (targetWidth, maximumEdge);
    }
}
