using System.Buffers.Binary;
using System.Runtime.InteropServices;
using FFmpeg.AutoGen;
using SkiaSharp;

namespace Ncm.Media;

internal static unsafe class FfmpegStillImageDecoder
{
    private const long MaximumPixelCount = 32_000_000;
    private const int MaximumStreamCount = 256;
    private const int MaximumTileCount = 256;
    private const int IoBufferSize = 32 * 1024;
    private static readonly avio_alloc_context_read_packet ReadPacketCallback = ReadPacket;
    private static readonly avio_alloc_context_seek SeekCallback = Seek;
    private static readonly AVIOInterruptCB_callback InterruptCallback = Interrupt;

    internal static SKBitmap Decode(byte[] data, string? extension, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ValidateFtypBrand(data, extension);
        if (ContainsAuxiliaryAlphaDeclaration(data))
        {
            throw new MediaExportException("HEIC/AVIF 的独立透明辅助图像暂不支持，请先将图片转换为带内嵌透明通道的 PNG。 ");
        }

        try
        {
            FfmpegAudioTranscoder.EnsureNativeLibrariesLoaded();
            using var input = new MemoryImageInput(data, cancellationToken);
            var formatContext = input.FormatContext;
            if (formatContext->nb_streams == 0 || formatContext->nb_streams > MaximumStreamCount)
            {
                throw new MediaExportException("HEIC/AVIF 图片的图像流数量无效或超过解码资源限制。");
            }

            var primaryGrid = FindPrimaryGrid(formatContext);
            if (primaryGrid is not null)
            {
                ValidateGrid(primaryGrid);
                var grid = primaryGrid->@params.tile_grid;
                var checkedStreams = new HashSet<int>();
                for (uint index = 0; index < grid->nb_tiles; index++)
                {
                    var stream = primaryGrid->streams[grid->offsets[index].idx];
                    if (stream is null || !checkedStreams.Add(stream->index))
                    {
                        continue;
                    }

                    ValidateCodec(stream, extension);
                }

                return DecodeGrid(input, primaryGrid, cancellationToken);
            }

            var primaryStream = FindPrimaryStream(formatContext);
            ValidateCodec(primaryStream, extension);
            ValidateDimensions(primaryStream->codecpar->width, primaryStream->codecpar->height);
            return DecodePrimaryStream(input, primaryStream, cancellationToken);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (MediaExportException)
        {
            throw;
        }
        catch (Exception exception) when (exception is DllNotFoundException or EntryPointNotFoundException or BadImageFormatException)
        {
            throw new MediaExportException("无法加载应用附带的 FFmpeg HEIC/AVIF 解码组件。", exception);
        }
        catch (OutOfMemoryException exception)
        {
            throw new MediaExportException("HEIC/AVIF 图片超出可用内存，请更换图片。", exception);
        }
        catch (Exception exception)
        {
            throw new MediaExportException("无法解码 HEIC/AVIF 图片。", exception);
        }
    }

    private static AVStreamGroup* FindPrimaryGrid(AVFormatContext* formatContext)
    {
        AVStreamGroup* selected = null;
        for (uint index = 0; index < formatContext->nb_stream_groups; index++)
        {
            var group = formatContext->stream_groups[index];
            if (group is null || group->type != AVStreamGroupParamsType.AV_STREAM_GROUP_PARAMS_TILE_GRID ||
                (group->disposition & ffmpeg.AV_DISPOSITION_DEFAULT) == 0)
            {
                continue;
            }

            if (selected is not null)
            {
                throw new MediaExportException("HEIC/AVIF 图片包含多个默认主图网格，无法确定要导入的封面。");
            }

            selected = group;
        }

        return selected;
    }

    private static AVStream* FindPrimaryStream(AVFormatContext* formatContext)
    {
        AVStream* selected = null;
        var videoStreamCount = 0;
        for (uint index = 0; index < formatContext->nb_streams; index++)
        {
            var stream = formatContext->streams[index];
            if (stream is null || stream->codecpar is null ||
                stream->codecpar->codec_type != AVMediaType.AVMEDIA_TYPE_VIDEO)
            {
                continue;
            }

            videoStreamCount++;
            if ((stream->disposition & ffmpeg.AV_DISPOSITION_DEFAULT) == 0)
            {
                continue;
            }

            if (selected is not null)
            {
                throw new MediaExportException("HEIC/AVIF 图片包含多个默认主图，无法确定要导入的封面。");
            }

            selected = stream;
        }

        if (selected is not null)
        {
            return selected;
        }

        if (videoStreamCount == 1)
        {
            for (uint index = 0; index < formatContext->nb_streams; index++)
            {
                var stream = formatContext->streams[index];
                if (stream is not null && stream->codecpar is not null &&
                    stream->codecpar->codec_type == AVMediaType.AVMEDIA_TYPE_VIDEO)
                {
                    return stream;
                }
            }
        }

        throw new MediaExportException("HEIC/AVIF 图片没有标记明确的主图，无法安全选择封面。");
    }

    private static void ValidateCodec(AVStream* stream, string? extension)
    {
        if (stream is null || stream->codecpar is null || stream->codecpar->codec_type != AVMediaType.AVMEDIA_TYPE_VIDEO)
        {
            throw new MediaExportException("HEIC/AVIF 主图不是有效的图像流。");
        }

        var codecId = stream->codecpar->codec_id;
        var matchesExtension = extension switch
        {
            ".avif" => codecId == AVCodecID.AV_CODEC_ID_AV1,
            ".heic" or ".heif" => codecId == AVCodecID.AV_CODEC_ID_HEVC,
            _ => codecId is AVCodecID.AV_CODEC_ID_AV1 or AVCodecID.AV_CODEC_ID_HEVC
        };
        if (!matchesExtension)
        {
            throw new MediaExportException("HEIC/AVIF 文件扩展名与主图编码格式不匹配。");
        }
    }

    private static void ValidateFtypBrand(ReadOnlySpan<byte> data, string? extension)
    {
        if (data.Length < 16 || !data.Slice(4, 4).SequenceEqual("ftyp"u8))
        {
            throw new MediaExportException("HEIC/AVIF 图片容器头无效。");
        }

        var boxSize32 = BinaryPrimitives.ReadUInt32BigEndian(data[..4]);
        var headerSize = 8;
        ulong boxSize;
        if (boxSize32 == 1)
        {
            if (data.Length < 24)
            {
                throw new MediaExportException("HEIC/AVIF 图片容器头不完整。");
            }

            boxSize = BinaryPrimitives.ReadUInt64BigEndian(data.Slice(8, 8));
            headerSize = 16;
        }
        else
        {
            boxSize = boxSize32 == 0 ? (ulong)data.Length : boxSize32;
        }

        if (boxSize < (ulong)headerSize + 8 || boxSize > (ulong)data.Length ||
            (boxSize - (ulong)headerSize - 8) % 4 != 0)
        {
            throw new MediaExportException("HEIC/AVIF 图片容器头无效。");
        }

        var boxEnd = (int)boxSize;
        var majorBrand = data.Slice(headerSize, 4);
        var hasHeifBrand = IsHeifBrand(majorBrand);
        var hasAvifBrand = IsAvifBrand(majorBrand);
        for (var position = headerSize + 8; position < boxEnd; position += 4)
        {
            var brand = data.Slice(position, 4);
            hasHeifBrand |= IsHeifBrand(brand);
            hasAvifBrand |= IsAvifBrand(brand);
        }

        var matchesExtension = extension switch
        {
            ".heic" or ".heif" => hasHeifBrand,
            ".avif" => hasAvifBrand,
            _ => hasHeifBrand || hasAvifBrand
        };
        if (!matchesExtension)
        {
            throw new MediaExportException("HEIC/AVIF 文件扩展名与图像容器品牌不匹配。");
        }
    }

    private static bool IsHeifBrand(ReadOnlySpan<byte> brand) =>
        brand.SequenceEqual("heic"u8) || brand.SequenceEqual("heix"u8) ||
        brand.SequenceEqual("hevc"u8) || brand.SequenceEqual("hevx"u8) ||
        brand.SequenceEqual("mif1"u8) || brand.SequenceEqual("msf1"u8) ||
        brand.SequenceEqual("heim"u8) || brand.SequenceEqual("heis"u8) ||
        brand.SequenceEqual("hevm"u8) || brand.SequenceEqual("hevs"u8) ||
        brand.SequenceEqual("avci"u8) || brand.SequenceEqual("avcs"u8);

    private static bool IsAvifBrand(ReadOnlySpan<byte> brand) =>
        brand.SequenceEqual("avif"u8) || brand.SequenceEqual("avis"u8);

    private static void ValidateGrid(AVStreamGroup* group)
    {
        var grid = group->@params.tile_grid;
        if (group->nb_streams is 0 or > MaximumStreamCount || group->streams is null ||
            grid is null || grid->nb_tiles is 0 or > MaximumTileCount || grid->offsets is null)
        {
            throw new MediaExportException("HEIC/AVIF 主图网格无效或超过解码资源限制。");
        }

        ValidateDimensions(grid->coded_width, grid->coded_height);
        ValidateDimensions(grid->width, grid->height);
        if (grid->horizontal_offset < 0 || grid->vertical_offset < 0 ||
            (long)grid->horizontal_offset + grid->width > grid->coded_width ||
            (long)grid->vertical_offset + grid->height > grid->coded_height)
        {
            throw new MediaExportException("HEIC/AVIF 主图网格裁剪范围无效。");
        }

        for (uint index = 0; index < grid->nb_tiles; index++)
        {
            var offset = grid->offsets[index];
            if (offset.idx >= group->nb_streams || offset.horizontal < 0 || offset.vertical < 0)
            {
                throw new MediaExportException("HEIC/AVIF 主图网格包含无效的图块位置。");
            }
        }
    }

    private static SKBitmap DecodePrimaryStream(
        MemoryImageInput input,
        AVStream* stream,
        CancellationToken cancellationToken)
    {
        using var decoder = new ImageStreamDecoder(stream, input.GetMaximumPixels(stream), cancellationToken);
        AVPacket* packet = null;
        AVFrame* frame = null;
        try
        {
            packet = input.AllocatePacket();
            frame = input.AllocateFrame();
            while (true)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var result = ffmpeg.av_read_frame(input.FormatContext, packet);
                if (result == ffmpeg.AVERROR_EOF)
                {
                    break;
                }

                Check(result, "读取 HEIC/AVIF 图像数据", cancellationToken);
                try
                {
                    if (packet->stream_index == stream->index && decoder.TryDecode(packet, frame))
                    {
                        var bitmap = DecodeFrame(frame, input.GetMaximumPixels(stream), cancellationToken);
                        return ApplyDisplayMatrix(bitmap, GetDisplayMatrix(frame, stream->codecpar->coded_side_data,
                            stream->codecpar->nb_coded_side_data), cancellationToken);
                    }
                }
                finally
                {
                    ffmpeg.av_packet_unref(packet);
                }
            }

            if (decoder.Flush(frame))
            {
                var bitmap = DecodeFrame(frame, input.GetMaximumPixels(stream), cancellationToken);
                return ApplyDisplayMatrix(bitmap, GetDisplayMatrix(frame, stream->codecpar->coded_side_data,
                    stream->codecpar->nb_coded_side_data), cancellationToken);
            }

            throw new MediaExportException("HEIC/AVIF 主图没有可解码的图像帧。");
        }
        finally
        {
            if (packet is not null)
            {
                ffmpeg.av_packet_free(&packet);
            }

            if (frame is not null)
            {
                ffmpeg.av_frame_free(&frame);
            }
        }
    }

    private static SKBitmap DecodeGrid(
        MemoryImageInput input,
        AVStreamGroup* group,
        CancellationToken cancellationToken)
    {
        var grid = group->@params.tile_grid;
        SKBitmap cropped;
        var displayMatrix = GetDisplayMatrix(grid->coded_side_data, grid->nb_coded_side_data);
        using (var canvas = new SKBitmap(new SKImageInfo(
            grid->coded_width,
            grid->coded_height,
            SKColorType.Rgba8888,
            SKAlphaType.Unpremul)))
        {
            if (!canvas.ReadyToDraw)
            {
                throw new MediaExportException("无法分配 HEIC/AVIF 主图网格画布。");
            }

            canvas.Erase(new SKColor(grid->background[0], grid->background[1], grid->background[2], grid->background[3]));

            var referencedStreamIndices = new HashSet<int>();
            for (uint index = 0; index < grid->nb_tiles; index++)
            {
                var offset = grid->offsets[index];
                var stream = group->streams[offset.idx];
                if (stream is null || stream->codecpar is null || stream->index < 0)
                {
                    throw new MediaExportException("HEIC/AVIF 主图网格引用了无效的图像流。");
                }

                referencedStreamIndices.Add(stream->index);
            }

            if (referencedStreamIndices.Count == 0 || referencedStreamIndices.Count > MaximumTileCount)
            {
                throw new MediaExportException("HEIC/AVIF 主图网格没有有效图块或图块过多。");
            }

            var streamsByIndex = new Dictionary<int, nint>();
            for (uint index = 0; index < group->nb_streams; index++)
            {
                var stream = group->streams[index];
                if (stream is not null && referencedStreamIndices.Contains(stream->index))
                {
                    streamsByIndex.TryAdd(stream->index, (nint)stream);
                }
            }

            if (streamsByIndex.Count != referencedStreamIndices.Count)
            {
                throw new MediaExportException("HEIC/AVIF 主图网格引用的图像流不完整。");
            }

            var decoders = new Dictionary<int, ImageStreamDecoder>();
            var decodedTiles = new Dictionary<int, SKBitmap>();
            long decodedPixelCount = 0;
            AVPacket* packet = null;
            AVFrame* frame = null;
            try
            {
                packet = input.AllocatePacket();
                frame = input.AllocateFrame();
                while (decodedTiles.Count < streamsByIndex.Count)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var result = ffmpeg.av_read_frame(input.FormatContext, packet);
                    if (result == ffmpeg.AVERROR_EOF)
                    {
                        break;
                    }

                    Check(result, "读取 HEIC/AVIF 图块数据", cancellationToken);
                    try
                    {
                        if (!streamsByIndex.TryGetValue(packet->stream_index, out var streamAddress))
                        {
                            continue;
                        }

                        var stream = (AVStream*)streamAddress;
                        if (!decoders.TryGetValue(stream->index, out var decoder))
                        {
                            decoder = new ImageStreamDecoder(stream, input.GetMaximumPixels(stream), cancellationToken);
                            decoders.Add(stream->index, decoder);
                        }

                        if (decoder.TryDecode(packet, frame))
                        {
                            var tilePixelCount = GetFramePixelCount(
                                frame,
                                input.GetMaximumPixels(stream),
                                decodedPixelCount);
                            var tile = DecodeFrame(frame, input.GetMaximumPixels(stream), cancellationToken);
                            StoreDecodedTile(decodedTiles, stream->index, tile, tilePixelCount, ref decodedPixelCount);
                            ffmpeg.av_frame_unref(frame);
                            decoder.Dispose();
                            decoders.Remove(stream->index);
                        }
                    }
                    finally
                    {
                        ffmpeg.av_packet_unref(packet);
                    }
                }

                foreach (var (streamIndex, decoder) in decoders.ToArray())
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    if (!decoder.Flush(frame))
                    {
                        continue;
                    }

                    var stream = (AVStream*)streamsByIndex[streamIndex];
                    var maximumPixels = input.GetMaximumPixels(stream);
                    var tilePixelCount = GetFramePixelCount(frame, maximumPixels, decodedPixelCount);
                    var tile = DecodeFrame(frame, maximumPixels, cancellationToken);
                    StoreDecodedTile(decodedTiles, streamIndex, tile, tilePixelCount, ref decodedPixelCount);
                    ffmpeg.av_frame_unref(frame);
                    decoder.Dispose();
                    decoders.Remove(streamIndex);
                }

                if (decodedTiles.Count != streamsByIndex.Count)
                {
                    throw new MediaExportException("HEIC/AVIF 主图网格包含无法解码的图块。");
                }

                for (uint index = 0; index < grid->nb_tiles; index++)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var offset = grid->offsets[index];
                    var stream = group->streams[offset.idx];
                    if (stream is null || !decodedTiles.TryGetValue(stream->index, out var tile))
                    {
                        throw new MediaExportException("HEIC/AVIF 主图网格引用了无法解码的图块。");
                    }

                    CompositeTile(canvas, tile, offset.horizontal, offset.vertical, cancellationToken);
                }

                foreach (var tile in decodedTiles.Values)
                {
                    tile.Dispose();
                }

                decodedTiles.Clear();
                cropped = Crop(canvas, grid->horizontal_offset, grid->vertical_offset, grid->width, grid->height);
            }
            finally
            {
                foreach (var decoder in decoders.Values)
                {
                    decoder.Dispose();
                }

                foreach (var tile in decodedTiles.Values)
                {
                    tile.Dispose();
                }

                if (packet is not null)
                {
                    ffmpeg.av_packet_free(&packet);
                }

                if (frame is not null)
                {
                    ffmpeg.av_frame_free(&frame);
                }
            }
        }

        return ApplyDisplayMatrix(cropped, displayMatrix, cancellationToken);
    }

    private static void StoreDecodedTile(
        Dictionary<int, SKBitmap> decodedTiles,
        int streamIndex,
        SKBitmap tile,
        long tilePixelCount,
        ref long decodedPixelCount)
    {
        if (decodedPixelCount > MaximumPixelCount - tilePixelCount)
        {
            tile.Dispose();
            throw new MediaExportException("HEIC/AVIF 图块累计像素超过解码资源限制。");
        }

        try
        {
            decodedTiles.Add(streamIndex, tile);
            decodedPixelCount += tilePixelCount;
        }
        catch
        {
            tile.Dispose();
            throw;
        }
    }

    private static long GetFramePixelCount(AVFrame* frame, long streamPixelLimit, long decodedPixelCount)
    {
        ValidateDimensions(frame->width, frame->height, streamPixelLimit);
        var pixelCount = (long)frame->width * frame->height;
        if (decodedPixelCount > MaximumPixelCount - pixelCount)
        {
            throw new MediaExportException("HEIC/AVIF 图块累计像素超过解码资源限制。");
        }

        return pixelCount;
    }

    private static SKBitmap DecodeFrame(AVFrame* frame, long maximumPixelCount, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ValidateDimensions(frame->width, frame->height, maximumPixelCount);
        if (frame->format < 0 || frame->data[0] is null)
        {
            throw new MediaExportException("HEIC/AVIF 解码器输出了无效图像帧。");
        }

        var bitmap = new SKBitmap(new SKImageInfo(
            frame->width,
            frame->height,
            SKColorType.Rgba8888,
            SKAlphaType.Unpremul));
        if (!bitmap.ReadyToDraw)
        {
            bitmap.Dispose();
            throw new MediaExportException("无法分配 HEIC/AVIF 解码图像。");
        }

        SwsContext* conversion = null;
        try
        {
            conversion = ffmpeg.sws_getContext(
                frame->width,
                frame->height,
                (AVPixelFormat)frame->format,
                frame->width,
                frame->height,
                AVPixelFormat.AV_PIX_FMT_RGBA,
                (int)SwsFlags.SWS_BILINEAR,
                null,
                null,
                null);
            if (conversion is null)
            {
                throw new MediaExportException("FFmpeg 不支持转换 HEIC/AVIF 图像像素格式。");
            }

            byte*[] sourceData = frame->data;
            int[] sourceLineSizes = frame->linesize;
            var destinationData = new byte*[4];
            destinationData[0] = (byte*)bitmap.GetPixels();
            var destinationLineSizes = new int[4];
            destinationLineSizes[0] = bitmap.RowBytes;
            var rows = ffmpeg.sws_scale(
                conversion,
                sourceData,
                sourceLineSizes,
                0,
                frame->height,
                destinationData,
                destinationLineSizes);
            if (rows != frame->height)
            {
                throw new MediaExportException("FFmpeg 未能完整转换 HEIC/AVIF 图像帧。");
            }

            cancellationToken.ThrowIfCancellationRequested();
            return bitmap;
        }
        catch
        {
            bitmap.Dispose();
            throw;
        }
        finally
        {
            if (conversion is not null)
            {
                ffmpeg.sws_freeContext(conversion);
            }
        }
    }

    private static void CompositeTile(
        SKBitmap canvas,
        SKBitmap tile,
        int left,
        int top,
        CancellationToken cancellationToken)
    {
        var canvasPixels = (byte*)canvas.GetPixels();
        var tilePixels = (byte*)tile.GetPixels();
        var width = Math.Min(tile.Width, canvas.Width - left);
        var height = Math.Min(tile.Height, canvas.Height - top);
        if (width <= 0 || height <= 0)
        {
            return;
        }

        for (var y = 0; y < height; y++)
        {
            if ((y & 31) == 0)
            {
                cancellationToken.ThrowIfCancellationRequested();
            }

            var sourceRow = tilePixels + y * tile.RowBytes;
            var destinationRow = canvasPixels + (top + y) * canvas.RowBytes + left * 4;
            for (var x = 0; x < width; x++)
            {
                var source = sourceRow + x * 4;
                var destination = destinationRow + x * 4;
                var sourceAlpha = source[3];
                if (sourceAlpha == 255)
                {
                    Buffer.MemoryCopy(source, destination, 4, 4);
                    continue;
                }

                if (sourceAlpha == 0)
                {
                    continue;
                }

                var destinationAlpha = destination[3];
                var remaining = 255 - sourceAlpha;
                var outputAlpha = sourceAlpha + (destinationAlpha * remaining + 127) / 255;
                if (outputAlpha == 0)
                {
                    destination[0] = 0;
                    destination[1] = 0;
                    destination[2] = 0;
                    destination[3] = 0;
                    continue;
                }

                for (var channel = 0; channel < 3; channel++)
                {
                    var premultiplied = source[channel] * sourceAlpha +
                        (destination[channel] * destinationAlpha * remaining + 127) / 255;
                    destination[channel] = (byte)((premultiplied + outputAlpha / 2) / outputAlpha);
                }

                destination[3] = (byte)outputAlpha;
            }
        }
    }

    private static SKBitmap Crop(SKBitmap source, int left, int top, int width, int height)
    {
        ValidateDimensions(width, height);
        var result = new SKBitmap(new SKImageInfo(width, height, SKColorType.Rgba8888, SKAlphaType.Unpremul));
        if (!result.ReadyToDraw)
        {
            result.Dispose();
            throw new MediaExportException("无法分配 HEIC/AVIF 主图裁剪图像。");
        }

        var sourcePixels = (byte*)source.GetPixels();
        var destinationPixels = (byte*)result.GetPixels();
        var copyLength = checked((long)width * 4);
        for (var y = 0; y < height; y++)
        {
            Buffer.MemoryCopy(
                sourcePixels + (top + y) * source.RowBytes + left * 4,
                destinationPixels + y * result.RowBytes,
                result.RowBytes,
                copyLength);
        }

        return result;
    }

    private static SKBitmap ApplyDisplayMatrix(
        SKBitmap source,
        int_array9? displayMatrix,
        CancellationToken cancellationToken)
    {
        if (displayMatrix is null)
        {
            return source;
        }

        try
        {
            var matrix = displayMatrix.Value;
            var rotation = ffmpeg.av_display_rotation_get(matrix);
            if (!double.IsFinite(rotation))
            {
                throw new MediaExportException("HEIC/AVIF 主图包含无效的显示矩阵。");
            }

            if (matrix[2] != 0 || matrix[5] != 0 || matrix[8] == 0)
            {
                throw new MediaExportException("HEIC/AVIF 主图包含暂不支持的透视显示矩阵。");
            }

            var a = matrix[0] / 65536d;
            var b = matrix[3] / 65536d;
            var c = matrix[1] / 65536d;
            var d = matrix[4] / 65536d;
            var translateX = matrix[6] / 65536d;
            var translateY = matrix[7] / 65536d;
            var determinant = a * d - b * c;
            if (!double.IsFinite(determinant) || Math.Abs(determinant) < 1e-8)
            {
                throw new MediaExportException("HEIC/AVIF 主图包含无效的显示矩阵。");
            }

            var corners = new (double X, double Y)[]
            {
                Transform(0, 0),
                Transform(source.Width, 0),
                Transform(0, source.Height),
                Transform(source.Width, source.Height)
            };
            var minimumX = Math.Floor(corners.Min(point => point.X) + 1e-7);
            var minimumY = Math.Floor(corners.Min(point => point.Y) + 1e-7);
            var maximumX = Math.Ceiling(corners.Max(point => point.X) - 1e-7);
            var maximumY = Math.Ceiling(corners.Max(point => point.Y) - 1e-7);
            var width = checked((int)(maximumX - minimumX));
            var height = checked((int)(maximumY - minimumY));
            ValidateDimensions(width, height);

            if (width == source.Width && height == source.Height &&
                Math.Abs(a - 1) < 1e-8 && Math.Abs(d - 1) < 1e-8 &&
                Math.Abs(b) < 1e-8 && Math.Abs(c) < 1e-8)
            {
                return source;
            }

            var result = new SKBitmap(new SKImageInfo(width, height, SKColorType.Rgba8888, SKAlphaType.Unpremul));
            if (!result.ReadyToDraw)
            {
                result.Dispose();
                throw new MediaExportException("无法分配旋转后的 HEIC/AVIF 主图。");
            }

            try
            {
                result.Erase(SKColors.Transparent);
                var sourcePixels = (byte*)source.GetPixels();
                var destinationPixels = (byte*)result.GetPixels();
                for (var y = 0; y < height; y++)
                {
                    if ((y & 31) == 0)
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                    }

                    var transformedY = minimumY + y + 0.5 - translateY;
                    for (var x = 0; x < width; x++)
                    {
                        var transformedX = minimumX + x + 0.5 - translateX;
                        var sourceX = (d * transformedX - c * transformedY) / determinant;
                        var sourceY = (-b * transformedX + a * transformedY) / determinant;
                        var sourceColumn = (int)Math.Floor(sourceX);
                        var sourceRow = (int)Math.Floor(sourceY);
                        if ((uint)sourceColumn >= (uint)source.Width || (uint)sourceRow >= (uint)source.Height)
                        {
                            continue;
                        }

                        Buffer.MemoryCopy(
                            sourcePixels + sourceRow * source.RowBytes + sourceColumn * 4,
                            destinationPixels + y * result.RowBytes + x * 4,
                            4,
                            4);
                    }
                }
            }
            catch
            {
                result.Dispose();
                throw;
            }

            source.Dispose();
            return result;

            (double X, double Y) Transform(double x, double y) =>
                (a * x + c * y + translateX, b * x + d * y + translateY);
        }
        catch
        {
            source.Dispose();
            throw;
        }
    }

    private static int_array9? GetDisplayMatrix(AVFrame* frame, AVPacketSideData* streamSideData, int streamSideDataCount)
    {
        var frameSideData = ffmpeg.av_frame_get_side_data(frame, AVFrameSideDataType.AV_FRAME_DATA_DISPLAYMATRIX);
        if (frameSideData is not null && frameSideData->size >= (ulong)sizeof(int_array9))
        {
            return *(int_array9*)frameSideData->data;
        }

        return GetDisplayMatrix(streamSideData, streamSideDataCount);
    }

    private static int_array9? GetDisplayMatrix(
        AVPacketSideData* sideData,
        int sideDataCount)
    {
        var matrixData = ffmpeg.av_packet_side_data_get(
            sideData,
            sideDataCount,
            AVPacketSideDataType.AV_PKT_DATA_DISPLAYMATRIX);
        if (matrixData is null || matrixData->size < (ulong)sizeof(int_array9))
        {
            return null;
        }

        return *(int_array9*)matrixData->data;
    }

    private static bool ContainsAuxiliaryAlphaDeclaration(ReadOnlySpan<byte> data) =>
        ScanBoxes(data, 0, data.Length, depth: 0, insideItemPropertyContainer: false);

    private static bool ScanBoxes(
        ReadOnlySpan<byte> data,
        int start,
        int end,
        int depth,
        bool insideItemPropertyContainer)
    {
        if (depth > 12)
        {
            return false;
        }

        var position = start;
        while (position <= end - 8)
        {
            var size32 = BinaryPrimitives.ReadUInt32BigEndian(data.Slice(position, 4));
            var type = data.Slice(position + 4, 4);
            var headerSize = 8;
            ulong boxSize;
            if (size32 == 1)
            {
                if (position > end - 16)
                {
                    return false;
                }

                boxSize = BinaryPrimitives.ReadUInt64BigEndian(data.Slice(position + 8, 8));
                headerSize = 16;
            }
            else
            {
                boxSize = size32 == 0 ? (ulong)(end - position) : size32;
            }

            if (boxSize < (uint)headerSize || boxSize > (ulong)(end - position))
            {
                return false;
            }

            var boxEnd = checked(position + (int)boxSize);
            var payloadStart = position + headerSize;
            if (insideItemPropertyContainer && type.SequenceEqual("auxC"u8) &&
                HasAlphaAuxiliaryType(data.Slice(payloadStart, boxEnd - payloadStart)))
            {
                return true;
            }

            if (type.SequenceEqual("meta"u8))
            {
                if (boxEnd - payloadStart >= 4 &&
                    ScanBoxes(data, payloadStart + 4, boxEnd, depth + 1, false))
                {
                    return true;
                }
            }
            else if (type.SequenceEqual("iprp"u8))
            {
                if (ScanBoxes(data, payloadStart, boxEnd, depth + 1, false))
                {
                    return true;
                }
            }
            else if (type.SequenceEqual("ipco"u8))
            {
                if (ScanBoxes(data, payloadStart, boxEnd, depth + 1, true))
                {
                    return true;
                }
            }

            position = boxEnd;
        }

        return false;
    }

    private static bool HasAlphaAuxiliaryType(ReadOnlySpan<byte> payload)
    {
        if (payload.Length < 5)
        {
            return false;
        }

        var auxiliaryType = payload[4..];
        var terminator = auxiliaryType.IndexOf((byte)0);
        if (terminator < 0)
        {
            return false;
        }

        auxiliaryType = auxiliaryType[..terminator];
        return auxiliaryType.SequenceEqual("urn:mpeg:hevc:2015:auxid:1"u8) ||
            auxiliaryType.SequenceEqual("urn:mpeg:mpegB:cicp:systems:auxiliary:alpha"u8);
    }

    private static void ValidateDimensions(int width, int height, long maximumPixelCount = MaximumPixelCount)
    {
        if (width <= 0 || height <= 0 || (long)width * height > maximumPixelCount)
        {
            throw new MediaExportException("HEIC/AVIF 图片尺寸无效或超过解码资源限制。");
        }
    }

    private static void Check(int result, string operation, CancellationToken cancellationToken)
    {
        if (result >= 0)
        {
            return;
        }

        if (cancellationToken.IsCancellationRequested || result == ffmpeg.AVERROR_EXIT)
        {
            throw new OperationCanceledException(cancellationToken);
        }

        byte* buffer = stackalloc byte[512];
        var errorResult = ffmpeg.av_strerror(result, buffer, 512);
        var message = errorResult < 0 ? $"错误码 {result}" : Marshal.PtrToStringUTF8((nint)buffer) ?? $"错误码 {result}";
        throw new MediaExportException($"FFmpeg {operation}失败：{message}");
    }

    private static int ReadPacket(void* opaque, byte* buffer, int bufferSize)
    {
        try
        {
            if (bufferSize <= 0)
            {
                return 0;
            }

            var state = (MemoryIoState)GCHandle.FromIntPtr((nint)opaque).Target!;
            if (state.CancellationToken.IsCancellationRequested)
            {
                return ffmpeg.AVERROR_EXIT;
            }

            var remaining = state.Data.LongLength - state.Position;
            if (remaining <= 0)
            {
                return ffmpeg.AVERROR_EOF;
            }

            var count = (int)Math.Min(bufferSize, remaining);
            fixed (byte* source = state.Data)
            {
                Buffer.MemoryCopy(source + state.Position, buffer, bufferSize, count);
            }

            state.Position += count;
            return count;
        }
        catch
        {
            return ffmpeg.AVERROR_EXTERNAL;
        }
    }

    private static long Seek(void* opaque, long offset, int whence)
    {
        try
        {
            var state = (MemoryIoState)GCHandle.FromIntPtr((nint)opaque).Target!;
            if (state.CancellationToken.IsCancellationRequested)
            {
                return ffmpeg.AVERROR_EXIT;
            }

            var seekMode = whence & ~ffmpeg.AVSEEK_FORCE;
            if (seekMode == ffmpeg.AVSEEK_SIZE)
            {
                return state.Data.LongLength;
            }

            var origin = seekMode switch
            {
                0 => 0L,
                1 => state.Position,
                2 => state.Data.LongLength,
                _ => -1L
            };
            var position = checked(origin + offset);
            if (origin < 0 || position < 0 || position > state.Data.LongLength)
            {
                return ffmpeg.AVERROR(22);
            }

            state.Position = position;
            return position;
        }
        catch
        {
            return ffmpeg.AVERROR_EXTERNAL;
        }
    }

    private static int Interrupt(void* opaque)
    {
        try
        {
            var state = (MemoryIoState)GCHandle.FromIntPtr((nint)opaque).Target!;
            return state.CancellationToken.IsCancellationRequested ? 1 : 0;
        }
        catch
        {
            return 1;
        }
    }

    private sealed class ImageStreamDecoder : IDisposable
    {
        private readonly CancellationToken _cancellationToken;
        private AVCodecContext* _codecContext;

        internal ImageStreamDecoder(AVStream* stream, long maximumPixelCount, CancellationToken cancellationToken)
        {
            _cancellationToken = cancellationToken;
            var codec = ffmpeg.avcodec_find_decoder(stream->codecpar->codec_id);
            if (codec is null)
            {
                throw new MediaExportException("FFmpeg 没有可用的 HEIC/AVIF 图像解码器。");
            }

            _codecContext = ffmpeg.avcodec_alloc_context3(codec);
            if (_codecContext is null)
            {
                throw new MediaExportException("FFmpeg 无法分配 HEIC/AVIF 解码上下文。");
            }

            try
            {
                Check(ffmpeg.avcodec_parameters_to_context(_codecContext, stream->codecpar), "读取 HEIC/AVIF 解码参数", cancellationToken);
                _codecContext->max_pixels = maximumPixelCount;
                _codecContext->thread_count = 1;
                _codecContext->apply_cropping = 1;
                Check(ffmpeg.avcodec_open2(_codecContext, codec, null), "打开 HEIC/AVIF 解码器", cancellationToken);
            }
            catch
            {
                Dispose();
                throw;
            }
        }

        internal bool TryDecode(AVPacket* packet, AVFrame* frame)
        {
            _cancellationToken.ThrowIfCancellationRequested();
            var result = ffmpeg.avcodec_send_packet(_codecContext, packet);
            if (result == ffmpeg.AVERROR(ffmpeg.EAGAIN))
            {
                if (TryReceive(frame))
                {
                    return true;
                }

                result = ffmpeg.avcodec_send_packet(_codecContext, packet);
            }

            Check(result, "提交 HEIC/AVIF 图像数据", _cancellationToken);
            return TryReceive(frame);
        }

        internal bool Flush(AVFrame* frame)
        {
            _cancellationToken.ThrowIfCancellationRequested();
            var result = ffmpeg.avcodec_send_packet(_codecContext, null);
            if (result == ffmpeg.AVERROR(ffmpeg.EAGAIN))
            {
                if (TryReceive(frame))
                {
                    return true;
                }

                result = ffmpeg.avcodec_send_packet(_codecContext, null);
            }

            if (result != ffmpeg.AVERROR_EOF)
            {
                Check(result, "刷新 HEIC/AVIF 解码器", _cancellationToken);
            }

            return TryReceive(frame);
        }

        private bool TryReceive(AVFrame* frame)
        {
            var result = ffmpeg.avcodec_receive_frame(_codecContext, frame);
            if (result == ffmpeg.AVERROR(ffmpeg.EAGAIN) || result == ffmpeg.AVERROR_EOF)
            {
                return false;
            }

            Check(result, "接收 HEIC/AVIF 图像帧", _cancellationToken);
            return true;
        }

        public void Dispose()
        {
            if (_codecContext is not null)
            {
                var codecContext = _codecContext;
                ffmpeg.avcodec_free_context(&codecContext);
                _codecContext = null;
            }
        }
    }

    private sealed class MemoryImageInput : IDisposable
    {
        private readonly MemoryIoState _state;
        private readonly Dictionary<int, long> _streamPixelLimits = [];
        private GCHandle _stateHandle;
        private AVFormatContext* _formatContext;
        private AVIOContext* _ioContext;
        private long _videoStreamPixelCount;

        internal MemoryImageInput(byte[] data, CancellationToken cancellationToken)
        {
            _state = new MemoryIoState(data, cancellationToken);
            _stateHandle = GCHandle.Alloc(_state);
            try
            {
                _formatContext = ffmpeg.avformat_alloc_context();
                if (_formatContext is null)
                {
                    throw new MediaExportException("FFmpeg 无法分配 HEIC/AVIF 输入上下文。");
                }

                var ioBuffer = (byte*)ffmpeg.av_malloc(IoBufferSize);
                if (ioBuffer is null)
                {
                    throw new MediaExportException("FFmpeg 无法分配 HEIC/AVIF 输入缓冲区。");
                }

                _ioContext = ffmpeg.avio_alloc_context(
                    ioBuffer,
                    IoBufferSize,
                    0,
                    (void*)GCHandle.ToIntPtr(_stateHandle),
                    ReadPacketCallback,
                    null,
                    SeekCallback);
                if (_ioContext is null)
                {
                    ffmpeg.av_free(ioBuffer);
                    throw new MediaExportException("FFmpeg 无法创建 HEIC/AVIF 内存输入流。");
                }

                _ioContext->seekable = ffmpeg.AVIO_SEEKABLE_NORMAL;
                _formatContext->pb = _ioContext;
                _formatContext->flags |= ffmpeg.AVFMT_FLAG_CUSTOM_IO;
                _formatContext->max_streams = MaximumStreamCount;
                _formatContext->probesize = Math.Min(data.LongLength, 2_000_000);
                _formatContext->max_analyze_duration = 5 * ffmpeg.AV_TIME_BASE;
                _formatContext->interrupt_callback = new AVIOInterruptCB
                {
                    callback = InterruptCallback,
                    opaque = (void*)GCHandle.ToIntPtr(_stateHandle)
                };

                var inputFormat = ffmpeg.av_find_input_format("mov");
                if (inputFormat is null)
                {
                    throw new MediaExportException("FFmpeg 没有可用的 ISO 图像容器解复用器。");
                }

                var formatContext = _formatContext;
                var openResult = ffmpeg.avformat_open_input(&formatContext, null, inputFormat, null);
                _formatContext = formatContext;
                Check(openResult, "打开 HEIC/AVIF 输入流", cancellationToken);
                FindStreamInfo(cancellationToken);
                cancellationToken.ThrowIfCancellationRequested();
            }
            catch
            {
                Dispose();
                throw;
            }
        }

        internal AVFormatContext* FormatContext => _formatContext;

        internal long GetMaximumPixels(AVStream* stream)
        {
            if (stream is not null && _streamPixelLimits.TryGetValue(stream->index, out var maximumPixels))
            {
                return maximumPixels;
            }

            throw new MediaExportException("HEIC/AVIF 图像流缺少像素资源限制。");
        }

        private void FindStreamInfo(CancellationToken cancellationToken)
        {
            if (_formatContext->nb_streams > MaximumStreamCount)
            {
                throw new MediaExportException("HEIC/AVIF 图片的图像流数量超过解码资源限制。");
            }

            var streamCount = (int)_formatContext->nb_streams;
            var options = stackalloc AVDictionary*[MaximumStreamCount];
            var streamPixelLimits = new long[streamCount];
            var unknownDimensionStreams = new List<int>();
            long knownPixelCount = 0;
            for (var index = 0; index < MaximumStreamCount; index++)
            {
                options[index] = null;
            }

            try
            {
                for (var index = 0; index < streamCount; index++)
                {
                    var stream = _formatContext->streams[index];
                    if (stream is null || stream->codecpar is null ||
                        stream->codecpar->codec_type != AVMediaType.AVMEDIA_TYPE_VIDEO)
                    {
                        continue;
                    }

                    var width = stream->codecpar->width;
                    var height = stream->codecpar->height;
                    if (width > 0 && height > 0)
                    {
                        ValidateDimensions(width, height);
                        var pixels = (long)width * height;
                        if (knownPixelCount > MaximumPixelCount - pixels)
                        {
                            throw new MediaExportException("HEIC/AVIF 视频流累计像素超过解码资源限制。");
                        }

                        knownPixelCount += pixels;
                        streamPixelLimits[index] = pixels;
                    }
                    else
                    {
                        unknownDimensionStreams.Add(index);
                    }

                }

                if (unknownDimensionStreams.Count > 0)
                {
                    var perStreamLimit = (MaximumPixelCount - knownPixelCount) / unknownDimensionStreams.Count;
                    if (perStreamLimit <= 0)
                    {
                        throw new MediaExportException("HEIC/AVIF 未知尺寸的视频流超过解码资源限制。");
                    }

                    foreach (var index in unknownDimensionStreams)
                    {
                        streamPixelLimits[index] = perStreamLimit;
                    }
                }

                for (var index = 0; index < streamCount; index++)
                {
                    var stream = _formatContext->streams[index];
                    if (stream is null || stream->codecpar is null ||
                        stream->codecpar->codec_type != AVMediaType.AVMEDIA_TYPE_VIDEO)
                    {
                        continue;
                    }

                    _streamPixelLimits[stream->index] = streamPixelLimits[index];
                    Check(ffmpeg.av_dict_set_int(&options[index], "max_pixels", streamPixelLimits[index], 0),
                        "设置 HEIC/AVIF 探测像素上限", cancellationToken);
                    Check(ffmpeg.av_dict_set_int(&options[index], "threads", 1, 0),
                        "设置 HEIC/AVIF 探测线程数", cancellationToken);
                }

                Check(ffmpeg.avformat_find_stream_info(_formatContext, options), "读取 HEIC/AVIF 图像流信息", cancellationToken);
                if (_formatContext->nb_streams != (uint)streamCount)
                {
                    throw new MediaExportException("HEIC/AVIF 探测期间图像流数量发生变化，已取消解码。");
                }

                for (var index = 0; index < streamCount; index++)
                {
                    var stream = _formatContext->streams[index];
                    if (stream is null || stream->codecpar is null ||
                        stream->codecpar->codec_type != AVMediaType.AVMEDIA_TYPE_VIDEO ||
                        stream->codecpar->width <= 0 || stream->codecpar->height <= 0)
                    {
                        continue;
                    }

                    var width = stream->codecpar->width;
                    var height = stream->codecpar->height;
                    ValidateDimensions(width, height, _streamPixelLimits[stream->index]);
                    var pixels = (long)width * height;
                    if (_videoStreamPixelCount > MaximumPixelCount - pixels)
                    {
                        throw new MediaExportException("HEIC/AVIF 视频流累计像素超过解码资源限制。");
                    }

                    _videoStreamPixelCount += pixels;
                }
            }
            finally
            {
                for (var index = 0; index < streamCount; index++)
                {
                    if (options[index] is not null)
                    {
                        ffmpeg.av_dict_free(&options[index]);
                    }
                }
            }
        }

        internal AVPacket* AllocatePacket()
        {
            var packet = ffmpeg.av_packet_alloc();
            if (packet is null)
            {
                throw new MediaExportException("FFmpeg 无法分配 HEIC/AVIF 图像包。");
            }

            return packet;
        }

        internal AVFrame* AllocateFrame()
        {
            var frame = ffmpeg.av_frame_alloc();
            if (frame is null)
            {
                throw new MediaExportException("FFmpeg 无法分配 HEIC/AVIF 解码帧。");
            }

            return frame;
        }

        public void Dispose()
        {
            if (_formatContext is not null)
            {
                var formatContext = _formatContext;
                ffmpeg.avformat_close_input(&formatContext);
                _formatContext = null;
            }

            if (_ioContext is not null)
            {
                ffmpeg.av_freep(&_ioContext->buffer);
                var ioContext = _ioContext;
                ffmpeg.avio_context_free(&ioContext);
                _ioContext = null;
            }

            if (_stateHandle.IsAllocated)
            {
                _stateHandle.Free();
            }
        }
    }

    private sealed class MemoryIoState(byte[] data, CancellationToken cancellationToken)
    {
        internal byte[] Data { get; } = data;
        internal CancellationToken CancellationToken { get; } = cancellationToken;
        internal long Position { get; set; }
    }
}
