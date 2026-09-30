namespace Ncm.Core;

internal static class NcmAudioCipher
{
    internal static byte[] CreateKeyBox(ReadOnlySpan<byte> key)
    {
        if (key.Length is < 1 or > 256)
        {
            throw new NcmFormatException("音频密钥长度无效。");
        }

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

    internal static void DecryptInPlace(Span<byte> data, ReadOnlySpan<byte> box, long audioOffset)
    {
        for (var i = 0; i < data.Length; i++)
        {
            var index = (int)((audioOffset + i + 1) & 0xff);
            var nested = (box[index] + index) & 0xff;
            data[i] ^= box[(box[index] + box[nested]) & 0xff];
        }
    }

    internal static async Task CopyAsync(
        Stream input,
        Stream output,
        byte[] key,
        long length,
        CancellationToken cancellationToken = default,
        IProgress<long>? progress = null)
    {
        if (length < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(length));
        }

        var box = CreateKeyBox(key);
        var buffer = new byte[32768];
        long offset = 0;

        while (offset < length)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var wanted = (int)Math.Min(buffer.Length, length - offset);
            var read = await input.ReadAsync(buffer.AsMemory(0, wanted), cancellationToken);
            if (read == 0)
            {
                throw new NcmFormatException("音频数据被截断。");
            }

            DecryptInPlace(buffer.AsSpan(0, read), box, offset);
            await output.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
            offset += read;
            progress?.Report(offset);
        }
    }
}
