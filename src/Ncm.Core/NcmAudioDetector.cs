namespace Ncm.Core;

internal static class NcmAudioDetector
{
    internal static async Task<NcmAudioFormat> IdentifyAsync(
        Stream input,
        long audioStart,
        long audioLength,
        ReadOnlyMemory<byte> keyBox,
        CancellationToken cancellationToken)
    {
        if (audioLength < 4)
        {
            throw new NcmFormatException("音频数据为空或过短。");
        }

        var first = new byte[4];
        await ReadAtAsync(input, audioStart, audioLength, 0, first, keyBox, cancellationToken);

        if (first.AsSpan().SequenceEqual("fLaC"u8))
        {
            await ValidateFlacAsync(input, audioStart, audioLength, keyBox, cancellationToken);
            return NcmAudioFormat.Flac;
        }

        var frameOffset = 0L;
        if (first.AsSpan(0, 3).SequenceEqual("ID3"u8))
        {
            var header = new byte[10];
            await ReadAtAsync(input, audioStart, audioLength, 0, header, keyBox, cancellationToken);
            if (header[3] is < 2 or > 4 || header[4] == 0xff ||
                (header[6] | header[7] | header[8] | header[9]) >= 0x80)
            {
                throw new NcmFormatException("ID3 头部无效。");
            }

            var tagLength = (header[6] << 21) | (header[7] << 14) | (header[8] << 7) | header[9];
            var footerLength = header[3] == 4 && (header[5] & 0x10) != 0 ? 10 : 0;
            frameOffset = 10L + tagLength + footerLength;
        }

        var frameHeader = new byte[4];
        await ReadAtAsync(input, audioStart, audioLength, frameOffset, frameHeader, keyBox, cancellationToken);
        var frameLength = GetMp3FrameLength(frameHeader);
        if (frameLength == 0 || frameLength > audioLength - frameOffset)
        {
            throw new NcmFormatException("未找到完整的 MP3 音频帧。");
        }

        var mpeg1 = ((frameHeader[1] >> 3) & 3) == 3;
        var mono = (frameHeader[3] & 0xc0) == 0xc0;
        var sideInfoLength = mpeg1 ? mono ? 17 : 32 : mono ? 9 : 17;
        var sideInfoOffset = 4 + ((frameHeader[1] & 1) == 0 ? 2 : 0);
        if (frameLength < sideInfoOffset + sideInfoLength)
        {
            throw new NcmFormatException("MP3 音频帧侧信息被截断。");
        }

        var sideInfo = new byte[mpeg1 ? 2 : 1];
        await ReadAtAsync(
            input,
            audioStart,
            audioLength,
            frameOffset + sideInfoOffset,
            sideInfo,
            keyBox,
            cancellationToken);
        var reservoirOffset = mpeg1 ? (sideInfo[0] << 1) | (sideInfo[1] >> 7) : sideInfo[0];
        if (reservoirOffset != 0)
        {
            throw new NcmFormatException("MP3 首帧引用了不存在的位储存器数据。");
        }

        return NcmAudioFormat.Mp3;
    }

    private static int GetMp3FrameLength(ReadOnlySpan<byte> header)
    {
        if (header[0] != 0xff || (header[1] & 0xe0) != 0xe0)
        {
            return 0;
        }

        var version = (header[1] >> 3) & 3;
        var layer = (header[1] >> 1) & 3;
        var bitrateIndex = (header[2] >> 4) & 15;
        var sampleIndex = (header[2] >> 2) & 3;
        if (version == 1 || layer != 1 || bitrateIndex is 0 or 15 || sampleIndex == 3)
        {
            return 0;
        }

        ReadOnlySpan<int> mpeg1Bitrates = [0, 32, 40, 48, 56, 64, 80, 96, 112, 128, 160, 192, 224, 256, 320, 0];
        ReadOnlySpan<int> mpeg2Bitrates = [0, 8, 16, 24, 32, 40, 48, 56, 64, 80, 96, 112, 128, 144, 160, 0];
        ReadOnlySpan<int> sampleRates = [44100, 48000, 32000];
        var bitrate = (version == 3 ? mpeg1Bitrates : mpeg2Bitrates)[bitrateIndex] * 1000;
        var sampleRate = sampleRates[sampleIndex] / (version == 3 ? 1 : version == 2 ? 2 : 4);
        var padding = (header[2] >> 1) & 1;
        return (version == 3 ? 144 : 72) * bitrate / sampleRate + padding;
    }

    private static async Task ValidateFlacAsync(
        Stream input,
        long audioStart,
        long audioLength,
        ReadOnlyMemory<byte> keyBox,
        CancellationToken cancellationToken)
    {
        var offset = 4L;
        for (var blockCount = 0; blockCount < 1024; blockCount++)
        {
            var header = new byte[4];
            await ReadAtAsync(input, audioStart, audioLength, offset, header, keyBox, cancellationToken);
            var blockType = header[0] & 0x7f;
            var blockLength = (header[1] << 16) | (header[2] << 8) | header[3];
            if (blockType == 127 || blockCount == 0 && (blockType != 0 || blockLength != 34))
            {
                throw new NcmFormatException("FLAC 元数据头部无效。");
            }

            offset += 4;
            if (blockLength > audioLength - offset)
            {
                throw new NcmFormatException("FLAC 元数据被截断。");
            }

            if (blockCount == 0)
            {
                var streamInfo = new byte[34];
                await ReadAtAsync(input, audioStart, audioLength, offset, streamInfo, keyBox, cancellationToken);
                var sampleRate = (streamInfo[10] << 12) | (streamInfo[11] << 4) | (streamInfo[12] >> 4);
                if (sampleRate == 0)
                {
                    throw new NcmFormatException("FLAC 采样率无效。");
                }
            }

            offset += blockLength;
            if ((header[0] & 0x80) != 0)
            {
                await ValidateFlacFrameHeaderAsync(input, audioStart, audioLength, offset, keyBox, cancellationToken);
                return;
            }
        }

        throw new NcmFormatException("FLAC 元数据块过多。");
    }

    private static async Task ValidateFlacFrameHeaderAsync(
        Stream input,
        long audioStart,
        long audioLength,
        long frameOffset,
        ReadOnlyMemory<byte> keyBox,
        CancellationToken cancellationToken)
    {
        var remaining = audioLength - frameOffset;
        if (remaining < 9)
        {
            throw new NcmFormatException("FLAC 音频帧被截断。");
        }

        var header = new byte[(int)Math.Min(32, remaining)];
        await ReadAtAsync(input, audioStart, audioLength, frameOffset, header, keyBox, cancellationToken);
        if (header[0] != 0xff || (header[1] & 0xfe) != 0xf8 ||
            (header[3] & 1) != 0 || (header[3] >> 4) > 10 ||
            ((header[3] >> 1) & 7) == 3)
        {
            throw new NcmFormatException("FLAC 音频帧头部无效。");
        }

        var blockSizeCode = header[2] >> 4;
        var sampleRateCode = header[2] & 15;
        if (blockSizeCode == 0 || sampleRateCode == 15)
        {
            throw new NcmFormatException("FLAC 音频帧参数无效。");
        }

        var codedNumberLength = GetFlacCodedNumberLength(header[4]);
        if (codedNumberLength == 0)
        {
            throw new NcmFormatException("FLAC 音频帧编号无效。");
        }

        for (var i = 1; i < codedNumberLength; i++)
        {
            if ((header[4 + i] & 0xc0) != 0x80)
            {
                throw new NcmFormatException("FLAC 音频帧编号无效。");
            }
        }

        var crcOffset = 4 + codedNumberLength;
        crcOffset += blockSizeCode == 6 ? 1 : blockSizeCode == 7 ? 2 : 0;
        crcOffset += sampleRateCode == 12 ? 1 : sampleRateCode is 13 or 14 ? 2 : 0;
        if (remaining < crcOffset + 4)
        {
            throw new NcmFormatException("FLAC 音频帧被截断。");
        }

        if (ComputeFlacHeaderCrc(header.AsSpan(0, crcOffset)) != header[crcOffset] ||
            (header[crcOffset + 1] & 0x80) != 0)
        {
            throw new NcmFormatException("FLAC 音频帧头部校验失败。");
        }
    }

    private static int GetFlacCodedNumberLength(byte first)
    {
        if (first < 0x80) return 1;
        if (first is >= 0xc0 and < 0xe0) return 2;
        if (first is >= 0xe0 and < 0xf0) return 3;
        if (first is >= 0xf0 and < 0xf8) return 4;
        if (first is >= 0xf8 and < 0xfc) return 5;
        if (first is >= 0xfc and < 0xfe) return 6;
        return first == 0xfe ? 7 : 0;
    }

    private static byte ComputeFlacHeaderCrc(ReadOnlySpan<byte> bytes)
    {
        byte crc = 0;
        foreach (var value in bytes)
        {
            crc ^= value;
            for (var bit = 0; bit < 8; bit++)
            {
                crc = (byte)((crc & 0x80) != 0 ? (crc << 1) ^ 0x07 : crc << 1);
            }
        }

        return crc;
    }

    private static async Task ReadAtAsync(
        Stream input,
        long audioStart,
        long audioLength,
        long relativeOffset,
        byte[] buffer,
        ReadOnlyMemory<byte> keyBox,
        CancellationToken cancellationToken)
    {
        if (relativeOffset < 0 || relativeOffset > audioLength - buffer.Length)
        {
            throw new NcmFormatException("音频数据被截断。");
        }

        input.Position = audioStart + relativeOffset;
        try
        {
            await input.ReadExactlyAsync(buffer, cancellationToken);
        }
        catch (EndOfStreamException exception)
        {
            throw new NcmFormatException("音频数据被截断。", exception);
        }

        NcmAudioCipher.DecryptInPlace(buffer, keyBox.Span, relativeOffset);
    }
}
