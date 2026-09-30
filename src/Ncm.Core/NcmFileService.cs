using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Ncm.Core;

public sealed class NcmFileService
{
    private const int MaxEncryptedKeyLength = 4096;
    private const int MaxMetadataLength = 1024 * 1024;
    private const int MaxCoverLength = 16 * 1024 * 1024;

    private static readonly byte[] CoreKey =
    [
        0x68, 0x7a, 0x48, 0x52, 0x41, 0x6d, 0x73, 0x6f,
        0x35, 0x6b, 0x49, 0x6e, 0x62, 0x61, 0x78, 0x57
    ];

    private static readonly byte[] MetadataKey =
    [
        0x23, 0x31, 0x34, 0x6c, 0x6a, 0x6b, 0x5f, 0x21,
        0x5c, 0x5d, 0x26, 0x30, 0x55, 0x3c, 0x27, 0x28
    ];

    public async Task<NcmInspection> InspectAsync(
        string inputPath,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(inputPath);
        var sourcePath = Path.GetFullPath(inputPath);
        await using var input = OpenInput(sourcePath);
        var parsed = await ParseAsync(input, sourcePath, cancellationToken);
        return parsed.Inspection;
    }

    public Task<NcmExportResult> ExportAsync(
        string inputPath,
        string outputDirectory,
        CancellationToken cancellationToken = default,
        IProgress<long>? progress = null) =>
        ExportWithTransformAsync(inputPath, outputDirectory, null, cancellationToken, progress);

    internal async Task<NcmExportResult> ExportWithTransformAsync(
        string inputPath,
        string outputDirectory,
        Func<string, NcmInspection, CancellationToken, Task>? transform,
        CancellationToken cancellationToken = default,
        IProgress<long>? progress = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(inputPath);
        ArgumentException.ThrowIfNullOrWhiteSpace(outputDirectory);

        var sourcePath = Path.GetFullPath(inputPath);
        var outputRoot = Path.GetFullPath(outputDirectory);
        await using var input = OpenInput(sourcePath);
        var parsed = await ParseAsync(input, sourcePath, cancellationToken);

        var sourceName = Path.GetFileNameWithoutExtension(sourcePath);
        if (string.IsNullOrWhiteSpace(sourceName))
        {
            throw new NcmFormatException("输入文件名不能为空。");
        }

        var extension = parsed.Inspection.Format == NcmAudioFormat.Mp3 ? ".mp3" : ".flac";
        var outputPath = Path.Combine(outputRoot, sourceName + extension);
        if (File.Exists(outputPath) || Directory.Exists(outputPath))
        {
            throw new IOException($"目标文件已存在：{outputPath}");
        }

        cancellationToken.ThrowIfCancellationRequested();
        Directory.CreateDirectory(outputRoot);

        var temporaryPath = Path.Combine(outputRoot, $".ncm-{Guid.NewGuid():N}.tmp{extension}");
        var committed = false;
        try
        {
            await using (var output = new FileStream(
                temporaryPath,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.None,
                32768,
                FileOptions.Asynchronous))
            {
                input.Position = parsed.AudioStart;
                await NcmAudioCipher.CopyAsync(
                    input,
                    output,
                    parsed.AudioKey,
                    parsed.Inspection.AudioLength,
                    cancellationToken,
                    progress);
                await output.FlushAsync(cancellationToken);
            }

            cancellationToken.ThrowIfCancellationRequested();
            if (transform is not null)
            {
                await transform(temporaryPath, parsed.Inspection, cancellationToken);
            }

            cancellationToken.ThrowIfCancellationRequested();
            File.Move(temporaryPath, outputPath, overwrite: false);
            committed = true;
            return new NcmExportResult(outputPath, parsed.Inspection);
        }
        finally
        {
            if (!committed && File.Exists(temporaryPath))
            {
                File.Delete(temporaryPath);
            }
        }
    }

    private static FileStream OpenInput(string path) => new(
        path,
        FileMode.Open,
        FileAccess.Read,
        FileShare.Read,
        32768,
        FileOptions.Asynchronous);

    private static async Task<ParsedNcm> ParseAsync(
        FileStream input,
        string sourcePath,
        CancellationToken cancellationToken)
    {
        var magic = new byte[8];
        await ReadRequiredAsync(input, magic, cancellationToken);
        if (!magic.AsSpan().SequenceEqual("CTENFDAM"u8))
        {
            throw new NcmFormatException("NCM 文件魔数无效。");
        }

        var gap = new byte[2];
        await ReadRequiredAsync(input, gap, cancellationToken);

        var keyLength = await ReadLengthAsync(input, cancellationToken);
        if (keyLength is 0 or > MaxEncryptedKeyLength || keyLength % 16 != 0)
        {
            throw new NcmFormatException("NCM 密钥段长度无效。");
        }

        var encryptedKey = await ReadSectionAsync(input, keyLength, MaxEncryptedKeyLength, cancellationToken);
        XorInPlace(encryptedKey, 0x64);
        var decryptedKey = DecryptAes(encryptedKey, CoreKey, "密钥段");
        if (!decryptedKey.AsSpan().StartsWith("neteasecloudmusic"u8) ||
            decryptedKey.Length is <= 17 or > 273)
        {
            throw new NcmFormatException("NCM 音频密钥无效。");
        }

        var audioKey = decryptedKey[17..];
        var metadataLength = await ReadLengthAsync(input, cancellationToken);
        var metadata = metadataLength == 0
            ? null
            : ParseMetadata(await ReadSectionAsync(input, metadataLength, MaxMetadataLength, cancellationToken));

        var crcAndCoverSpace = new byte[9];
        await ReadRequiredAsync(input, crcAndCoverSpace, cancellationToken);
        var coverSpace = BinaryPrimitives.ReadUInt32LittleEndian(crcAndCoverSpace.AsSpan(5, 4));
        var coverLength = await ReadLengthAsync(input, cancellationToken);
        if (coverSpace > MaxCoverLength || coverLength > coverSpace)
        {
            throw new NcmFormatException("NCM 封面长度无效。");
        }

        if (coverSpace > input.Length - input.Position)
        {
            throw new NcmFormatException("NCM 封面数据被截断。");
        }

        byte[]? coverData = null;
        if (coverLength != 0)
        {
            coverData = await ReadSectionAsync(input, coverLength, MaxCoverLength, cancellationToken);
        }

        input.Position += coverSpace - coverLength;
        var audioStart = input.Position;
        var audioLength = input.Length - audioStart;
        var keyBox = NcmAudioCipher.CreateKeyBox(audioKey);
        var format = await NcmAudioDetector.IdentifyAsync(
            input,
            audioStart,
            audioLength,
            keyBox,
            cancellationToken);

        var inspection = new NcmInspection(sourcePath, format, audioLength, metadata, coverData);
        return new ParsedNcm(inspection, audioStart, audioKey);
    }

    private static NcmMetadata ParseMetadata(byte[] encryptedMetadata)
    {
        XorInPlace(encryptedMetadata, 0x63);
        if (!encryptedMetadata.AsSpan().StartsWith("163 key(Don't modify):"u8))
        {
            throw new NcmFormatException("NCM 元数据前缀无效。");
        }

        byte[] ciphertext;
        try
        {
            var base64 = Encoding.ASCII.GetString(encryptedMetadata, 22, encryptedMetadata.Length - 22);
            ciphertext = Convert.FromBase64String(base64);
        }
        catch (FormatException exception)
        {
            throw new NcmFormatException("NCM 元数据 Base64 无效。", exception);
        }

        var plaintext = DecryptAes(ciphertext, MetadataKey, "元数据");
        if (!plaintext.AsSpan().StartsWith("music:"u8))
        {
            throw new NcmFormatException("NCM 元数据内容无效。");
        }

        try
        {
            using var document = JsonDocument.Parse(plaintext.AsMemory(6), new JsonDocumentOptions { MaxDepth = 16 });
            if (document.RootElement.ValueKind != JsonValueKind.Object)
            {
                throw new NcmFormatException("NCM 元数据 JSON 根节点无效。");
            }

            var root = document.RootElement;
            var artists = new List<string>();
            if (root.TryGetProperty("artist", out var artistElement) && artistElement.ValueKind == JsonValueKind.Array)
            {
                foreach (var artist in artistElement.EnumerateArray())
                {
                    if (artist.ValueKind == JsonValueKind.Array && artist.GetArrayLength() > 0 &&
                        artist[0].ValueKind == JsonValueKind.String && artist[0].GetString() is { Length: > 0 } name)
                    {
                        artists.Add(name);
                    }
                }
            }

            return new NcmMetadata(
                GetOptionalString(root, "musicName"),
                GetOptionalString(root, "album"),
                artists,
                GetOptionalString(root, "format"),
                GetOptionalPositiveInt64(root, "albumId"));
        }
        catch (JsonException exception)
        {
            throw new NcmFormatException("NCM 元数据 JSON 无效。", exception);
        }
    }

    private static string? GetOptionalString(JsonElement root, string name) =>
        root.TryGetProperty(name, out var element) && element.ValueKind == JsonValueKind.String
            ? element.GetString()
            : null;

    private static long? GetOptionalPositiveInt64(JsonElement root, string name) =>
        root.TryGetProperty(name, out var element) && element.ValueKind == JsonValueKind.Number &&
        element.TryGetInt64(out var value) && value > 0
            ? value
            : null;

    private static byte[] DecryptAes(byte[] ciphertext, byte[] key, string section)
    {
        if (ciphertext.Length == 0 || ciphertext.Length % 16 != 0)
        {
            throw new NcmFormatException($"NCM {section} AES 块长度无效。");
        }

        try
        {
            using var aes = Aes.Create();
            aes.Key = key;
            return aes.DecryptEcb(ciphertext, PaddingMode.PKCS7);
        }
        catch (CryptographicException exception)
        {
            throw new NcmFormatException($"NCM {section} AES 填充或密文无效。", exception);
        }
    }

    private static void XorInPlace(Span<byte> data, byte value)
    {
        for (var i = 0; i < data.Length; i++)
        {
            data[i] ^= value;
        }
    }

    private static async Task<uint> ReadLengthAsync(Stream input, CancellationToken cancellationToken)
    {
        var bytes = new byte[4];
        await ReadRequiredAsync(input, bytes, cancellationToken);
        return BinaryPrimitives.ReadUInt32LittleEndian(bytes);
    }

    private static async Task<byte[]> ReadSectionAsync(
        Stream input,
        uint length,
        int maximum,
        CancellationToken cancellationToken)
    {
        if (length > maximum || length > input.Length - input.Position)
        {
            throw new NcmFormatException("NCM 数据段长度超出限制或文件范围。");
        }

        var buffer = new byte[(int)length];
        await ReadRequiredAsync(input, buffer, cancellationToken);
        return buffer;
    }

    private static async Task ReadRequiredAsync(
        Stream input,
        byte[] buffer,
        CancellationToken cancellationToken)
    {
        try
        {
            await input.ReadExactlyAsync(buffer, cancellationToken);
        }
        catch (EndOfStreamException exception)
        {
            throw new NcmFormatException("NCM 文件被截断。", exception);
        }
    }

    private sealed record ParsedNcm(NcmInspection Inspection, long AudioStart, byte[] AudioKey);
}
