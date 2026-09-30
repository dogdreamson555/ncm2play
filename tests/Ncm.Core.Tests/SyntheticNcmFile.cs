using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using Ncm.Core;

namespace Ncm.Core.Tests;

internal enum SyntheticMetadataMode
{
    Valid,
    Missing,
    InvalidBase64,
    InvalidJson,
    InvalidPadding
}

internal sealed record SyntheticNcmFixture(
    byte[] FileBytes,
    byte[] AudioBytes,
    int KeyLengthOffset,
    int KeyPayloadOffset,
    int MetadataLengthOffset,
    int MetadataPayloadOffset,
    int CoverSpaceOffset,
    int CoverLengthOffset,
    int CoverPayloadOffset,
    int AudioOffset);

internal static class SyntheticNcmFile
{
    private static readonly byte[] AudioSeed = Encoding.ASCII.GetBytes("independent-test-key");
    private static readonly byte[] CoreKey = Encoding.ASCII.GetBytes("hzHRAmso5kInbaxW");
    private static readonly byte[] MetadataKey = [0x23, 0x31, 0x34, 0x6c, 0x6a, 0x6b, 0x5f, 0x21, 0x5c, 0x5d, 0x26, 0x30, 0x55, 0x3c, 0x27, 0x28];
    private static readonly byte[] NcmAudioPrefix = Encoding.ASCII.GetBytes("neteasecloudmusic");
    private static readonly byte[] MetadataPrefix = Encoding.ASCII.GetBytes("163 key(Don't modify):");

    public static SyntheticNcmFixture Create(
        NcmAudioFormat format,
        SyntheticMetadataMode metadataMode = SyntheticMetadataMode.Valid,
        byte[]? cover = null,
        bool reserveCoverSpace = false,
        byte[]? audioBytes = null,
        bool invalidKeyPadding = false,
        long? albumId = null)
    {
        var audio = audioBytes ?? SyntheticAudio.Create(format);
        var keyPlaintext = new byte[NcmAudioPrefix.Length + AudioSeed.Length];
        NcmAudioPrefix.CopyTo(keyPlaintext, 0);
        AudioSeed.CopyTo(keyPlaintext, NcmAudioPrefix.Length);
        var keyPayload = invalidKeyPadding
            ? EncryptWithInvalidPkcs7(CoreKey, keyPlaintext)
            : EncryptPkcs7(CoreKey, keyPlaintext);
        XorInPlace(keyPayload, 0x64);

        var metadataSection = CreateMetadataPayload(metadataMode, format, albumId);
        var encodedAudio = EncryptAudioPayload(audio, AudioSeed);
        var image = cover ?? [];
        var coverSpace = image.Length + (reserveCoverSpace && image.Length > 0 ? 7 : 0);

        using var stream = new MemoryStream();
        stream.Write("CTENFDAM"u8);
        stream.Write([0x5a, 0xa5]);

        var keyLengthOffset = checked((int)stream.Position);
        WriteUInt32(stream, checked((uint)keyPayload.Length));
        var keyPayloadOffset = checked((int)stream.Position);
        stream.Write(keyPayload);

        var metadataLengthOffset = checked((int)stream.Position);
        WriteUInt32(stream, checked((uint)metadataSection.Length));
        var metadataPayloadOffset = checked((int)stream.Position);
        stream.Write(metadataSection);

        stream.Write(new byte[4]);
        stream.WriteByte(0);
        var coverSpaceOffset = checked((int)stream.Position);
        WriteUInt32(stream, checked((uint)coverSpace));
        var coverLengthOffset = checked((int)stream.Position);
        WriteUInt32(stream, checked((uint)image.Length));
        var coverPayloadOffset = checked((int)stream.Position);
        stream.Write(image);
        if (coverSpace > image.Length)
        {
            stream.Write(Enumerable.Repeat((byte)0xa7, coverSpace - image.Length).ToArray());
        }

        var audioOffset = checked((int)stream.Position);
        stream.Write(encodedAudio);

        return new SyntheticNcmFixture(
            stream.ToArray(),
            audio,
            keyLengthOffset,
            keyPayloadOffset,
            metadataLengthOffset,
            metadataPayloadOffset,
            coverSpaceOffset,
            coverLengthOffset,
            coverPayloadOffset,
            audioOffset);
    }

    private static byte[] CreateMetadataPayload(SyntheticMetadataMode mode, NcmAudioFormat format, long? albumId)
    {
        if (mode == SyntheticMetadataMode.Missing)
        {
            return [];
        }

        if (mode == SyntheticMetadataMode.InvalidBase64)
        {
            var invalid = MetadataPrefix.Concat(Encoding.ASCII.GetBytes("***")).ToArray();
            XorInPlace(invalid, 0x63);
            return invalid;
        }

        var declaredFormat = format == NcmAudioFormat.Mp3 ? "flac" : "mp3";
        var json = mode == SyntheticMetadataMode.InvalidJson
            ? "{invalid-json"
            : "{\"musicId\":123,\"musicName\":\"Synthetic Track\",\"artist\":[[\"Synthetic Artist\",1]],\"album\":\"Synthetic Album\",\"format\":\"" + declaredFormat + "\"}";
        if (mode == SyntheticMetadataMode.Valid && albumId.HasValue)
        {
            json = json[..^1] + ",\"albumId\":" + albumId.Value.ToString(System.Globalization.CultureInfo.InvariantCulture) + "}";
        }
        var plaintext = Encoding.UTF8.GetBytes("music:" + json);
        var encrypted = mode == SyntheticMetadataMode.InvalidPadding
            ? EncryptWithInvalidPkcs7(MetadataKey, plaintext)
            : EncryptPkcs7(MetadataKey, plaintext);
        var base64 = MetadataPrefix.Concat(Encoding.ASCII.GetBytes(Convert.ToBase64String(encrypted))).ToArray();
        XorInPlace(base64, 0x63);
        return base64;
    }

    private static byte[] EncryptPkcs7(byte[] key, byte[] plaintext)
    {
        using var aes = Aes.Create();
        aes.Key = key;
        aes.Mode = CipherMode.ECB;
        aes.Padding = PaddingMode.PKCS7;
        using var encryptor = aes.CreateEncryptor();
        return encryptor.TransformFinalBlock(plaintext, 0, plaintext.Length);
    }

    private static byte[] EncryptWithInvalidPkcs7(byte[] key, byte[] plaintext)
    {
        var padding = 16 - plaintext.Length % 16;
        var padded = new byte[plaintext.Length + padding];
        plaintext.CopyTo(padded, 0);
        if (padding == 1)
        {
            padded[^1] = 0;
        }

        using var aes = Aes.Create();
        aes.Key = key;
        aes.Mode = CipherMode.ECB;
        aes.Padding = PaddingMode.None;
        using var encryptor = aes.CreateEncryptor();
        return encryptor.TransformFinalBlock(padded, 0, padded.Length);
    }

    internal static byte[] EncryptAudioPayload(byte[] plaintext, byte[] key)
    {
        var box = CreateKeyBox(key);
        var output = (byte[])plaintext.Clone();
        for (var i = 0; i < output.Length; i++)
        {
            var index = (i + 1) & 0xff;
            var nested = (box[index] + index) & 0xff;
            output[i] ^= box[(box[index] + box[nested]) & 0xff];
        }

        return output;
    }

    private static byte[] CreateKeyBox(byte[] key)
    {
        var box = new byte[256];
        for (var i = 0; i < box.Length; i++)
        {
            box[i] = (byte)i;
        }

        var previous = 0;
        var keyOffset = 0;
        for (var i = 0; i < box.Length; i++)
        {
            var index = (box[i] + previous + key[keyOffset]) & 0xff;
            (box[i], box[index]) = (box[index], box[i]);
            previous = index;
            keyOffset = (keyOffset + 1) % key.Length;
        }

        return box;
    }

    private static void WriteUInt32(Stream stream, uint value)
    {
        Span<byte> bytes = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32LittleEndian(bytes, value);
        stream.Write(bytes);
    }

    private static void XorInPlace(byte[] bytes, byte value)
    {
        for (var i = 0; i < bytes.Length; i++)
        {
            bytes[i] ^= value;
        }
    }
}

internal static class SyntheticAudio
{
    public static byte[] Create(NcmAudioFormat format) => format switch
    {
        NcmAudioFormat.Mp3 => CreateMp3(3),
        NcmAudioFormat.Flac => CreateFlac(),
        _ => throw new ArgumentOutOfRangeException(nameof(format))
    };

    public static byte[] CreateMp3(int frameCount)
    {
        const int frameLength = 417;
        var bytes = new byte[checked(frameLength * frameCount)];
        for (var frame = 0; frame < frameCount; frame++)
        {
            var offset = frame * frameLength;
            bytes[offset] = 0xff;
            bytes[offset + 1] = 0xfb;
            bytes[offset + 2] = 0x90;
            bytes[offset + 3] = 0x64;
        }

        return bytes;
    }

    private static byte[] CreateFlac()
    {
        using var stream = new MemoryStream();
        stream.Write("fLaC"u8);
        stream.Write([0x80, 0x00, 0x00, 0x22]);

        Span<byte> streamInfo = stackalloc byte[34];
        BinaryPrimitives.WriteUInt16BigEndian(streamInfo, 16);
        BinaryPrimitives.WriteUInt16BigEndian(streamInfo[2..], 16);
        const ulong sampleRateChannelsBitsSamples = ((ulong)44100 << 44) | ((ulong)15 << 36) | 16;
        BinaryPrimitives.WriteUInt64BigEndian(streamInfo[10..], sampleRateChannelsBitsSamples);
        MD5.HashData(new byte[32], streamInfo[18..34]);
        stream.Write(streamInfo);

        Span<byte> frame = stackalloc byte[12];
        frame[0] = 0xff;
        frame[1] = 0xf8;
        frame[2] = 0x60;
        frame[3] = 0x00;
        frame[4] = 0x00;
        frame[5] = 0x0f;
        frame[6] = ComputeCrc8(frame[..6]);
        frame[7] = 0x00;
        frame[8] = 0x00;
        frame[9] = 0x00;
        var crc = ComputeCrc16(frame[..10]);
        BinaryPrimitives.WriteUInt16BigEndian(frame[10..], crc);
        stream.Write(frame);
        return stream.ToArray();
    }

    private static byte ComputeCrc8(ReadOnlySpan<byte> bytes)
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

    private static ushort ComputeCrc16(ReadOnlySpan<byte> bytes)
    {
        ushort crc = 0;
        foreach (var value in bytes)
        {
            crc ^= (ushort)(value << 8);
            for (var bit = 0; bit < 8; bit++)
            {
                crc = (ushort)((crc & 0x8000) != 0 ? (crc << 1) ^ 0x8005 : crc << 1);
            }
        }

        return crc;
    }
}
