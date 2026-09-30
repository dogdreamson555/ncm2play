using System.Security.Cryptography;
using System.Text;
using System.Buffers.Binary;
using Ncm.Core;

namespace Ncm.Core.Tests;

public sealed class NcmFileServiceTests
{
    private static readonly byte[] TestCover = [0xff, 0xd8, 0xff, 0xe0, 0x00, 0x04, 0x4e, 0x43, 0x4d, 0xff, 0xd9];

    [Theory]
    [InlineData(NcmAudioFormat.Mp3, "mp3", "446904869DAC43A5B9995E32D37A1C5CF22C5268ED588DA712D27F3AA31FBB67")]
    [InlineData(NcmAudioFormat.Flac, "flac", "7CF1AB605934D08A2A2C842E5C9A3B3E85B4DC3E0D7AC8F5A18883615E7D9F72")]
    public async Task InspectAndExportPreserveOriginalAudio(
        NcmAudioFormat actualFormat,
        string extension,
        string expectedAudioHash)
    {
        var temporaryDirectory = Directory.CreateTempSubdirectory();
        try
        {
            var inputPath = Path.Combine(temporaryDirectory.FullName, "generated.ncm");
            var outputDirectory = Path.Combine(temporaryDirectory.FullName, "out");
            var fixture = SyntheticNcmFile.Create(
                actualFormat,
                cover: TestCover,
                reserveCoverSpace: true);
            await File.WriteAllBytesAsync(inputPath, fixture.FileBytes);
            var sourceHashBefore = SHA256.HashData(await File.ReadAllBytesAsync(inputPath));
            var service = new NcmFileService();

            var inspection = await service.InspectAsync(inputPath);

            Assert.Equal(actualFormat, inspection.Format);
            Assert.Equal(fixture.AudioBytes.LongLength, inspection.AudioLength);
            Assert.NotNull(inspection.Metadata);
            Assert.Equal("Synthetic Track", inspection.Metadata.Title);
            Assert.Equal("Synthetic Album", inspection.Metadata.Album);
            Assert.Equal(new[] { "Synthetic Artist" }, inspection.Metadata.Artists);
            Assert.Equal(actualFormat == NcmAudioFormat.Mp3 ? "flac" : "mp3", inspection.Metadata.DeclaredFormat);
            Assert.Equal(TestCover, inspection.CoverData);

            var exported = await service.ExportAsync(inputPath, outputDirectory);
            var outputBytes = await File.ReadAllBytesAsync(exported.OutputPath);

            Assert.Equal(Path.Combine(outputDirectory, $"generated.{extension}"), exported.OutputPath);
            Assert.Equal(actualFormat, exported.Inspection.Format);
            Assert.Equal(fixture.AudioBytes.LongLength, outputBytes.LongLength);
            var actualAudioHash = Convert.ToHexString(SHA256.HashData(outputBytes));
            Assert.Equal(expectedAudioHash, actualAudioHash);
            Assert.Equal(fixture.AudioBytes, outputBytes);
            Assert.Equal(sourceHashBefore, SHA256.HashData(await File.ReadAllBytesAsync(inputPath)));
        }
        finally
        {
            temporaryDirectory.Delete(recursive: true);
        }
    }

    [Theory]
    [InlineData(null)]
    [InlineData(5000000001L)]
    [InlineData(0L)]
    [InlineData(-1L)]
    public async Task MetadataRetainsOnlyValidAlbumIds(long? albumId)
    {
        var temporaryDirectory = Directory.CreateTempSubdirectory();
        try
        {
            var inputPath = Path.Combine(temporaryDirectory.FullName, "album-id.ncm");
            var fixture = SyntheticNcmFile.Create(NcmAudioFormat.Mp3, albumId: albumId);
            await File.WriteAllBytesAsync(inputPath, fixture.FileBytes);

            var inspection = await new NcmFileService().InspectAsync(inputPath);

            Assert.NotNull(inspection.Metadata);
            Assert.Equal(albumId is > 0 ? albumId : null, inspection.Metadata.AlbumId);
        }
        finally
        {
            temporaryDirectory.Delete(recursive: true);
        }
    }

    [Theory]
    [InlineData(NcmAudioFormat.Mp3)]
    [InlineData(NcmAudioFormat.Flac)]
    public async Task MissingMetadataAndCoverAreReportedAsNull(NcmAudioFormat format)
    {
        var temporaryDirectory = Directory.CreateTempSubdirectory();
        try
        {
            var inputPath = Path.Combine(temporaryDirectory.FullName, "without-optional-data.ncm");
            var fixture = SyntheticNcmFile.Create(format, metadataMode: SyntheticMetadataMode.Missing);
            await File.WriteAllBytesAsync(inputPath, fixture.FileBytes);

            var inspection = await new NcmFileService().InspectAsync(inputPath);

            Assert.Null(inspection.Metadata);
            Assert.Null(inspection.CoverData);
        }
        finally
        {
            temporaryDirectory.Delete(recursive: true);
        }
    }

    [Fact]
    public async Task MissingMetadataDoesNotDiscardCover()
    {
        var temporaryDirectory = Directory.CreateTempSubdirectory();
        try
        {
            var inputPath = Path.Combine(temporaryDirectory.FullName, "cover-only.ncm");
            var fixture = SyntheticNcmFile.Create(
                NcmAudioFormat.Mp3,
                metadataMode: SyntheticMetadataMode.Missing,
                cover: TestCover);
            await File.WriteAllBytesAsync(inputPath, fixture.FileBytes);

            var inspection = await new NcmFileService().InspectAsync(inputPath);

            Assert.Null(inspection.Metadata);
            Assert.Equal(TestCover, inspection.CoverData);
        }
        finally
        {
            temporaryDirectory.Delete(recursive: true);
        }
    }

    [Fact]
    public async Task MissingCoverDoesNotDiscardMetadata()
    {
        var temporaryDirectory = Directory.CreateTempSubdirectory();
        try
        {
            var inputPath = Path.Combine(temporaryDirectory.FullName, "metadata-only.ncm");
            var fixture = SyntheticNcmFile.Create(NcmAudioFormat.Flac);
            await File.WriteAllBytesAsync(inputPath, fixture.FileBytes);

            var inspection = await new NcmFileService().InspectAsync(inputPath);

            Assert.NotNull(inspection.Metadata);
            Assert.Null(inspection.CoverData);
        }
        finally
        {
            temporaryDirectory.Delete(recursive: true);
        }
    }

    [Theory]
    [InlineData(MalformedNcmKind.WrongMagic)]
    [InlineData(MalformedNcmKind.WrongSecondMagic)]
    [InlineData(MalformedNcmKind.TruncatedHeader)]
    [InlineData(MalformedNcmKind.TruncatedKey)]
    [InlineData(MalformedNcmKind.KeyLengthOverrun)]
    [InlineData(MalformedNcmKind.KeyLengthNotBlockAligned)]
    [InlineData(MalformedNcmKind.TruncatedMetadata)]
    [InlineData(MalformedNcmKind.MetadataLengthOverrun)]
    [InlineData(MalformedNcmKind.CoverLengthExceedsSpace)]
    [InlineData(MalformedNcmKind.CoverLengthOverrun)]
    [InlineData(MalformedNcmKind.TruncatedCover)]
    [InlineData(MalformedNcmKind.InvalidBase64)]
    [InlineData(MalformedNcmKind.InvalidJson)]
    [InlineData(MalformedNcmKind.InvalidPkcs7Padding)]
    [InlineData(MalformedNcmKind.InvalidKeyPkcs7Padding)]
    public async Task ExportRejectsMalformedNcmAndLeavesNoFiles(MalformedNcmKind kind)
    {
        var temporaryDirectory = Directory.CreateTempSubdirectory();
        try
        {
            var inputPath = Path.Combine(temporaryDirectory.FullName, "damaged.ncm");
            var outputDirectory = Path.Combine(temporaryDirectory.FullName, "out");
            Directory.CreateDirectory(outputDirectory);
            var bytes = MakeMalformedFixture(kind);
            await File.WriteAllBytesAsync(inputPath, bytes);

            await Assert.ThrowsAsync<NcmFormatException>(() => new NcmFileService().ExportAsync(inputPath, outputDirectory));

            Assert.Empty(Directory.EnumerateFileSystemEntries(outputDirectory));
        }
        finally
        {
            temporaryDirectory.Delete(recursive: true);
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task InspectRejectsEmptyOrUnrecognizedAudio(bool empty)
    {
        var temporaryDirectory = Directory.CreateTempSubdirectory();
        try
        {
            var inputPath = Path.Combine(temporaryDirectory.FullName, "invalid-audio.ncm");
            var invalidAudio = empty ? [] : Encoding.ASCII.GetBytes("nope-not-an-audio-frame");
            var fixture = SyntheticNcmFile.Create(NcmAudioFormat.Mp3, audioBytes: invalidAudio);
            await File.WriteAllBytesAsync(inputPath, fixture.FileBytes);

            await Assert.ThrowsAsync<NcmFormatException>(() => new NcmFileService().InspectAsync(inputPath));
        }
        finally
        {
            temporaryDirectory.Delete(recursive: true);
        }
    }

    [Fact]
    public async Task ExportRejectsMp3FrameReferencingMissingReservoirData()
    {
        var temporaryDirectory = Directory.CreateTempSubdirectory();
        try
        {
            var inputPath = Path.Combine(temporaryDirectory.FullName, "fake-mp3.ncm");
            var outputDirectory = Path.Combine(temporaryDirectory.FullName, "out");
            var fakeAudio = Enumerable.Repeat((byte)0xff, 417).ToArray();
            fakeAudio[1] = 0xfb;
            fakeAudio[2] = 0x90;
            fakeAudio[3] = 0x64;
            await File.WriteAllBytesAsync(inputPath,
                SyntheticNcmFile.Create(NcmAudioFormat.Mp3, audioBytes: fakeAudio).FileBytes);

            await Assert.ThrowsAsync<NcmFormatException>(() =>
                new NcmFileService().ExportAsync(inputPath, outputDirectory));

            Assert.False(Directory.Exists(outputDirectory));
        }
        finally
        {
            temporaryDirectory.Delete(recursive: true);
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ExportRejectsTruncatedOrCorruptFlacFrame(bool badCrc)
    {
        var temporaryDirectory = Directory.CreateTempSubdirectory();
        try
        {
            var inputPath = Path.Combine(temporaryDirectory.FullName, "fake-flac.ncm");
            var outputDirectory = Path.Combine(temporaryDirectory.FullName, "out");
            var audio = SyntheticAudio.Create(NcmAudioFormat.Flac);
            if (badCrc)
            {
                audio[48] ^= 0xff;
            }
            else
            {
                audio = audio[..46];
            }

            await File.WriteAllBytesAsync(inputPath,
                SyntheticNcmFile.Create(NcmAudioFormat.Flac, audioBytes: audio).FileBytes);

            await Assert.ThrowsAsync<NcmFormatException>(() =>
                new NcmFileService().ExportAsync(inputPath, outputDirectory));

            Assert.False(Directory.Exists(outputDirectory));
        }
        finally
        {
            temporaryDirectory.Delete(recursive: true);
        }
    }

    [Theory]
    [InlineData(NcmAudioFormat.Mp3, "mp3")]
    [InlineData(NcmAudioFormat.Flac, "flac")]
    public async Task ExportDoesNotOverwriteExistingFile(NcmAudioFormat format, string extension)
    {
        var temporaryDirectory = Directory.CreateTempSubdirectory();
        try
        {
            var inputPath = Path.Combine(temporaryDirectory.FullName, "same-name.ncm");
            var outputDirectory = Path.Combine(temporaryDirectory.FullName, "out");
            var existingOutputPath = Path.Combine(outputDirectory, $"same-name.{extension}");
            var sentinel = Encoding.UTF8.GetBytes("existing user content");
            Directory.CreateDirectory(outputDirectory);
            await File.WriteAllBytesAsync(inputPath, SyntheticNcmFile.Create(format).FileBytes);
            await File.WriteAllBytesAsync(existingOutputPath, sentinel);

            await Assert.ThrowsAnyAsync<IOException>(() => new NcmFileService().ExportAsync(inputPath, outputDirectory));

            Assert.Equal(sentinel, await File.ReadAllBytesAsync(existingOutputPath));
            Assert.Equal(existingOutputPath, Assert.Single(Directory.EnumerateFiles(outputDirectory)));
        }
        finally
        {
            temporaryDirectory.Delete(recursive: true);
        }
    }

    [Fact]
    public async Task OutputCreatedDuringExportIsPreservedAndTemporaryFileIsRemoved()
    {
        var temporaryDirectory = Directory.CreateTempSubdirectory();
        try
        {
            var inputPath = Path.Combine(temporaryDirectory.FullName, "racing.ncm");
            var outputDirectory = Path.Combine(temporaryDirectory.FullName, "out");
            var outputPath = Path.Combine(outputDirectory, "racing.mp3");
            var sentinel = Encoding.UTF8.GetBytes("created by another process");
            Directory.CreateDirectory(outputDirectory);
            await File.WriteAllBytesAsync(inputPath, SyntheticNcmFile.Create(NcmAudioFormat.Mp3).FileBytes);
            var progress = new CreateFileOnFirstProgress(outputPath, sentinel);

            await Assert.ThrowsAnyAsync<IOException>(() =>
                new NcmFileService().ExportAsync(inputPath, outputDirectory, progress: progress));

            Assert.True(progress.WasCalled);
            Assert.Equal(sentinel, await File.ReadAllBytesAsync(outputPath));
            Assert.Equal(outputPath, Assert.Single(Directory.EnumerateFiles(outputDirectory)));
        }
        finally
        {
            temporaryDirectory.Delete(recursive: true);
        }
    }

    [Fact]
    public async Task CancellationAfterTemporaryFileCreationCleansTemporaryOutput()
    {
        var temporaryDirectory = Directory.CreateTempSubdirectory();
        try
        {
            var inputPath = Path.Combine(temporaryDirectory.FullName, "large.ncm");
            var outputDirectory = Path.Combine(temporaryDirectory.FullName, "out");
            Directory.CreateDirectory(outputDirectory);
            var audio = SyntheticAudio.CreateMp3(32768);
            await File.WriteAllBytesAsync(inputPath, SyntheticNcmFile.Create(NcmAudioFormat.Mp3, audioBytes: audio).FileBytes);

            using var cancellationSource = new CancellationTokenSource();
            var progress = new CancelOnFirstProgress(cancellationSource);

            await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
                new NcmFileService().ExportAsync(inputPath, outputDirectory, cancellationSource.Token, progress));

            Assert.True(progress.ReportedBytes > 0);
            Assert.Empty(Directory.EnumerateFileSystemEntries(outputDirectory));
        }
        finally
        {
            temporaryDirectory.Delete(recursive: true);
        }
    }

    private static byte[] MakeMalformedFixture(MalformedNcmKind kind)
    {
        var metadataMode = kind switch
        {
            MalformedNcmKind.InvalidBase64 => SyntheticMetadataMode.InvalidBase64,
            MalformedNcmKind.InvalidJson => SyntheticMetadataMode.InvalidJson,
            MalformedNcmKind.InvalidPkcs7Padding => SyntheticMetadataMode.InvalidPadding,
            _ => SyntheticMetadataMode.Valid
        };
        var fixture = SyntheticNcmFile.Create(
            NcmAudioFormat.Mp3,
            metadataMode,
            TestCover,
            invalidKeyPadding: kind == MalformedNcmKind.InvalidKeyPkcs7Padding);
        var bytes = (byte[])fixture.FileBytes.Clone();
        switch (kind)
        {
            case MalformedNcmKind.WrongMagic:
                bytes[0] ^= 0xff;
                break;
            case MalformedNcmKind.WrongSecondMagic:
                bytes[4] ^= 0xff;
                break;
            case MalformedNcmKind.TruncatedHeader:
                return bytes[..7];
            case MalformedNcmKind.TruncatedKey:
                return bytes[..(fixture.KeyPayloadOffset + 1)];
            case MalformedNcmKind.KeyLengthOverrun:
                BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(fixture.KeyLengthOffset), uint.MaxValue);
                break;
            case MalformedNcmKind.KeyLengthNotBlockAligned:
                BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(fixture.KeyLengthOffset), 15);
                break;
            case MalformedNcmKind.TruncatedMetadata:
                return bytes[..(fixture.CoverSpaceOffset - 6)];
            case MalformedNcmKind.MetadataLengthOverrun:
                BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(fixture.MetadataLengthOffset), uint.MaxValue);
                break;
            case MalformedNcmKind.CoverLengthExceedsSpace:
                BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(fixture.CoverSpaceOffset), 0);
                break;
            case MalformedNcmKind.CoverLengthOverrun:
                BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(fixture.CoverLengthOffset), uint.MaxValue);
                break;
            case MalformedNcmKind.TruncatedCover:
                return bytes[..(fixture.CoverPayloadOffset + TestCover.Length - 1)];
            case MalformedNcmKind.InvalidBase64:
            case MalformedNcmKind.InvalidJson:
            case MalformedNcmKind.InvalidPkcs7Padding:
            case MalformedNcmKind.InvalidKeyPkcs7Padding:
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(kind));
        }

        return bytes;
    }

    public enum MalformedNcmKind
    {
        WrongMagic,
        WrongSecondMagic,
        TruncatedHeader,
        TruncatedKey,
        KeyLengthOverrun,
        KeyLengthNotBlockAligned,
        TruncatedMetadata,
        MetadataLengthOverrun,
        CoverLengthExceedsSpace,
        CoverLengthOverrun,
        TruncatedCover,
        InvalidBase64,
        InvalidJson,
        InvalidPkcs7Padding,
        InvalidKeyPkcs7Padding
    }

    private sealed class CancelOnFirstProgress(CancellationTokenSource cancellationSource) : IProgress<long>
    {
        public long ReportedBytes { get; private set; }

        public void Report(long value)
        {
            ReportedBytes = value;
            cancellationSource.Cancel();
        }
    }

    private sealed class CreateFileOnFirstProgress(string path, byte[] bytes) : IProgress<long>
    {
        public bool WasCalled { get; private set; }

        public void Report(long value)
        {
            if (!WasCalled)
            {
                WasCalled = true;
                File.WriteAllBytes(path, bytes);
            }
        }
    }
}
