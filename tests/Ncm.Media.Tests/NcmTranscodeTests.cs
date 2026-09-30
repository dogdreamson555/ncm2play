using System.Diagnostics;
using Ncm.Core;
using Ncm.Core.Tests;
using Ncm.Media;
using SkiaSharp;
using TagLib;

namespace Ncm.Media.Tests;

public sealed class NcmTranscodeTests
{
    [Fact]
    public async Task PreviewReportsRequiredMp3SampleRateAdjustment()
    {
        using var workspace = new TranscodeWorkspace();
        var input = await workspace.CreateInputAsync(
            NcmAudioFormat.Flac,
            "tone-96000-stereo-s24.flac");

        var preview = await new NcmMediaService().PreviewAsync(
            input,
            new MediaExportOptions(AudioFormat: AudioExportFormat.Mp3, Mp3BitrateKbps: 320));

        Assert.Equal(96000, preview.SourceSampleRate);
        Assert.Equal(48000, preview.OutputSampleRate);
        Assert.Equal(2, preview.OutputChannels);
        Assert.Contains(preview.Adjustments, message => message.Contains("采样率"));
    }

    [Theory]
    [InlineData(NcmAudioFormat.Mp3, "tone-44100-stereo-mp3-128k.mp3")]
    [InlineData(NcmAudioFormat.Flac, "tone-44100-stereo-s16.flac")]
    public async Task OriginalFormatKeepsCompressedAudioFramesUnchanged(
        NcmAudioFormat format,
        string fixtureName)
    {
        using var workspace = new TranscodeWorkspace();
        var input = await workspace.CreateInputAsync(format, fixtureName, withCover: true);

        var result = await new NcmMediaService().ExportAsync(input, workspace.OutputDirectory);

        Assert.Equal(format, result.OutputFormat);
        var original = await System.IO.File.ReadAllBytesAsync(
            Path.Combine(AppContext.BaseDirectory, "Fixtures", fixtureName));
        var exported = await System.IO.File.ReadAllBytesAsync(result.OutputPath);
        Assert.Equal(AudioPayload(original, format), AudioPayload(exported, format));
    }

    [Theory]
    [InlineData(128)]
    [InlineData(192)]
    [InlineData(256)]
    [InlineData(320)]
    public async Task Mp3QualityProducesConstantFrameBitrateAndFinalTags(int bitrate)
    {
        using var workspace = new TranscodeWorkspace();
        var input = await workspace.CreateInputAsync(
            NcmAudioFormat.Flac,
            "tone-44100-stereo-s16.flac",
            withCover: true);

        var result = await new NcmMediaService().ExportAsync(
            input,
            workspace.OutputDirectory,
            new MediaExportOptions(AudioFormat: AudioExportFormat.Mp3, Mp3BitrateKbps: bitrate));

        Assert.Equal(NcmAudioFormat.Mp3, result.OutputFormat);
        Assert.EndsWith(".mp3", result.OutputPath, StringComparison.OrdinalIgnoreCase);
        using var output = TagLib.File.Create(result.OutputPath);
        Assert.Equal("Synthetic Track", output.Tag.Title);
        Assert.Equal("Synthetic Artist", Assert.Single(output.Tag.Performers));
        Assert.Equal("Synthetic Album", output.Tag.Album);
        Assert.Single(output.Tag.Pictures);
        Assert.Equal(44100, output.Properties.AudioSampleRate);
        Assert.Equal(2, output.Properties.AudioChannels);
        AssertMp3FrameBitrate(result.OutputPath, bitrate);
    }

    [Theory]
    [InlineData("tone-44100-stereo-s16.flac", 0, 44100, 16)]
    [InlineData("tone-44100-stereo-s16.flac", 8, 44100, 16)]
    [InlineData("tone-96000-stereo-s24.flac", 8, 96000, 24)]
    public async Task FlacCompressionKeepsSampleRateChannelsAndDepth(
        string fixtureName,
        int level,
        int sampleRate,
        int bitsPerSample)
    {
        using var workspace = new TranscodeWorkspace();
        var input = await workspace.CreateInputAsync(NcmAudioFormat.Flac, fixtureName);

        var result = await new NcmMediaService().ExportAsync(
            input,
            workspace.OutputDirectory,
            new MediaExportOptions(AudioFormat: AudioExportFormat.Flac, FlacCompressionLevel: level));

        Assert.Equal(NcmAudioFormat.Flac, result.OutputFormat);
        using var output = TagLib.File.Create(result.OutputPath);
        Assert.Equal("Synthetic Track", output.Tag.Title);
        Assert.Equal(sampleRate, output.Properties.AudioSampleRate);
        Assert.Equal(2, output.Properties.AudioChannels);
        Assert.Equal(bitsPerSample, output.Properties.BitsPerSample);
        var pcmFormat = bitsPerSample == 16 ? "pcm_s16le" : "pcm_s32le";
        var sourceHash = await DecodePcmHashAsync(
            Path.Combine(AppContext.BaseDirectory, "Fixtures", fixtureName),
            pcmFormat);
        var outputHash = await DecodePcmHashAsync(result.OutputPath, pcmFormat);
        Assert.Equal(sourceHash, outputHash);
    }

    [Fact]
    public async Task Mp3ToFlacIsSupportedAndPreviewExplainsLossySource()
    {
        using var workspace = new TranscodeWorkspace();
        var input = await workspace.CreateInputAsync(
            NcmAudioFormat.Mp3,
            "tone-44100-stereo-mp3-128k.mp3");
        var options = new MediaExportOptions(AudioFormat: AudioExportFormat.Flac);

        var preview = await new NcmMediaService().PreviewAsync(input, options);
        var result = await new NcmMediaService().ExportAsync(input, workspace.OutputDirectory, options);

        Assert.Contains(preview.Adjustments, message => message.Contains("不会恢复"));
        Assert.Equal(NcmAudioFormat.Flac, result.OutputFormat);
        using var output = TagLib.File.Create(result.OutputPath);
        Assert.Equal(44100, output.Properties.AudioSampleRate);
        Assert.Equal("Synthetic Track", output.Tag.Title);
    }

    [Fact]
    public async Task TranscodingKeepsSourceTagsAndAudioCoverWhenNcmDataIsMissing()
    {
        using var workspace = new TranscodeWorkspace();
        var input = await workspace.CreateInputAsync(
            NcmAudioFormat.Flac,
            "tone-44100-stereo-s16.flac",
            withAudioTags: true);

        var result = await new NcmMediaService().ExportAsync(
            input,
            workspace.OutputDirectory,
            new MediaExportOptions(AudioFormat: AudioExportFormat.Mp3));

        Assert.Equal(CoverOrigin.Audio, result.CoverOrigin);
        using var output = TagLib.File.Create(result.OutputPath);
        Assert.Equal("Source title", output.Tag.Title);
        Assert.Equal("Source artist", Assert.Single(output.Tag.Performers));
        Assert.Equal("Source album", output.Tag.Album);
        Assert.Equal("Keep comment", output.Tag.Comment);
        Assert.Equal("Electronic", Assert.Single(output.Tag.Genres));
        Assert.Equal((uint)2024, output.Tag.Year);
        Assert.Equal((uint)3, output.Tag.Track);
        Assert.Single(output.Tag.Pictures);
    }

    [Fact]
    public async Task CancellationRemovesPartialTranscodeAndDecryptedStage()
    {
        using var workspace = new TranscodeWorkspace();
        var input = await workspace.CreateInputAsync(
            NcmAudioFormat.Flac,
            "tone-44100-stereo-s16.flac");
        var original = await System.IO.File.ReadAllBytesAsync(input);
        var transcoder = new BlockingTranscoder();
        var service = new NcmMediaService(new NcmFileService(), transcoder);
        using var cancellation = new CancellationTokenSource();

        var export = service.ExportAsync(
            input,
            workspace.OutputDirectory,
            new MediaExportOptions(AudioFormat: AudioExportFormat.Mp3),
            cancellation.Token);
        await transcoder.Started.Task.WaitAsync(TimeSpan.FromSeconds(10));
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => export);
        Assert.Equal(original, await System.IO.File.ReadAllBytesAsync(input));
        Assert.Empty(Directory.EnumerateFileSystemEntries(workspace.OutputDirectory));
    }

    [Fact]
    public async Task EncoderFailureRemovesPartialOutput()
    {
        using var workspace = new TranscodeWorkspace();
        var input = await workspace.CreateInputAsync(
            NcmAudioFormat.Flac,
            "tone-44100-stereo-s16.flac");
        var service = new NcmMediaService(new NcmFileService(), new FailingTranscoder());

        await Assert.ThrowsAsync<MediaExportException>(() => service.ExportAsync(
            input,
            workspace.OutputDirectory,
            new MediaExportOptions(AudioFormat: AudioExportFormat.Mp3)));

        Assert.Empty(Directory.EnumerateFileSystemEntries(workspace.OutputDirectory));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task MismatchedFormatOrDurationIsNotCommitted(bool wrongFormat)
    {
        using var workspace = new TranscodeWorkspace();
        var input = await workspace.CreateInputAsync(
            NcmAudioFormat.Flac,
            "tone-44100-stereo-s16.flac");
        var service = new NcmMediaService(
            new NcmFileService(),
            new MismatchedTranscoder(wrongFormat));

        await Assert.ThrowsAsync<MediaExportException>(() => service.ExportAsync(
            input,
            workspace.OutputDirectory,
            new MediaExportOptions(AudioFormat: AudioExportFormat.Mp3)));

        Assert.Empty(Directory.EnumerateFileSystemEntries(workspace.OutputDirectory));
    }

    [Theory]
    [InlineData(AudioExportFormat.Mp3, 44100, 0.01)]
    [InlineData(AudioExportFormat.Flac, 44100, 0.01)]
    [InlineData(AudioExportFormat.Mp3, 8000, 0.1)]
    [InlineData(AudioExportFormat.Flac, 8000, 0.1)]
    public async Task ShortMp3SourceRejectsSeverelyShortenedOutput(
        AudioExportFormat outputFormat, int sampleRate, double reportedOutputDuration)
    {
        using var workspace = new TranscodeWorkspace();
        var fixture = sampleRate == 8000 ? "tone-8000-stereo-mp3-64k.mp3" : "tone-44100-stereo-mp3-128k.mp3";
        var input = await workspace.CreateInputAsync(NcmAudioFormat.Mp3, fixture);
        var service = new NcmMediaService(new NcmFileService(),
            new ShortOutputTranscoder(outputFormat, sampleRate, reportedOutputDuration));

        var error = await Assert.ThrowsAsync<MediaExportException>(() => service.ExportAsync(
            input,
            workspace.OutputDirectory,
            new MediaExportOptions(AudioFormat: outputFormat, Mp3BitrateKbps: 128)));

        Assert.Contains("时长", error.Message);
        Assert.Empty(Directory.EnumerateFileSystemEntries(workspace.OutputDirectory));
    }

    [Theory]
    [InlineData("tone-44100-stereo-mp3-128k.mp3", 44100, 320)]
    [InlineData("tone-8000-stereo-mp3-64k.mp3", 16000, 128)]
    public async Task ShortMp3SourceCanBeReencodedWithNormalEncoderPadding(string fixture, int sampleRate, int bitrate)
    {
        using var workspace = new TranscodeWorkspace();
        var input = await workspace.CreateInputAsync(NcmAudioFormat.Mp3, fixture);

        var result = await new NcmMediaService().ExportAsync(
            input,
            workspace.OutputDirectory,
            new MediaExportOptions(AudioFormat: AudioExportFormat.Mp3, Mp3BitrateKbps: bitrate));

        using var output = TagLib.File.Create(result.OutputPath);
        Assert.InRange(output.Properties.Duration.TotalSeconds, 0.45, 0.85);
        Assert.Equal(sampleRate, output.Properties.AudioSampleRate);
        Assert.Equal("Synthetic Track", output.Tag.Title);
        AssertMp3FrameBitrate(result.OutputPath, bitrate);
    }

    [Fact]
    public async Task UnexpectedMp3BitrateIsNotCommitted()
    {
        using var workspace = new TranscodeWorkspace();
        var input = await workspace.CreateInputAsync(NcmAudioFormat.Mp3, "tone-44100-stereo-mp3-128k.mp3");
        var service = new NcmMediaService(new NcmFileService(),
            new ShortOutputTranscoder(AudioExportFormat.Mp3, 44100, 0.5, reportedBitrateKbps: 64));

        var error = await Assert.ThrowsAsync<MediaExportException>(() => service.ExportAsync(
            input, workspace.OutputDirectory,
            new MediaExportOptions(AudioFormat: AudioExportFormat.Mp3, Mp3BitrateKbps: 128)));

        Assert.Contains("码率", error.Message);
        Assert.Empty(Directory.EnumerateFileSystemEntries(workspace.OutputDirectory));
    }

    private static void AssertMp3FrameBitrate(string path, int expectedBitrate)
    {
        var data = System.IO.File.ReadAllBytes(path);
        var position = 0;
        if (data.AsSpan().StartsWith("ID3"u8))
        {
            position = 10 + ((data[6] & 0x7f) << 21) + ((data[7] & 0x7f) << 14) +
                ((data[8] & 0x7f) << 7) + (data[9] & 0x7f);
            if ((data[5] & 0x10) != 0)
            {
                position += 10;
            }
        }

        var checkedFrames = 0;
        while (position + 4 <= data.Length && checkedFrames < 8)
        {
            var header = System.Buffers.Binary.BinaryPrimitives.ReadUInt32BigEndian(data.AsSpan(position, 4));
            var version = (header >> 19) & 3;
            if ((header & 0xffe00000) != 0xffe00000 || version == 1 || ((header >> 17) & 3) != 1)
            {
                if (checkedFrames == 0)
                {
                    position++;
                    continue;
                }

                break;
            }

            var bitrateIndex = (int)((header >> 12) & 0x0f);
            var sampleRateIndex = (int)((header >> 10) & 3);
            Assert.InRange(bitrateIndex, 1, 14);
            Assert.InRange(sampleRateIndex, 0, 2);
            var bitrates = version == 3
                ? new[] { 0, 32, 40, 48, 56, 64, 80, 96, 112, 128, 160, 192, 224, 256, 320 }
                : new[] { 0, 8, 16, 24, 32, 40, 48, 56, 64, 80, 96, 112, 128, 144, 160 };
            var sampleRates = new[] { 44100, 48000, 32000 };
            var sampleRate = sampleRates[sampleRateIndex] / (version == 3 ? 1 : version == 2 ? 2 : 4);
            Assert.Equal(expectedBitrate, bitrates[bitrateIndex]);
            var padding = (int)((header >> 9) & 1);
            position += (version == 3 ? 144000 : 72000) * bitrates[bitrateIndex] / sampleRate + padding;
            checkedFrames++;
        }

        Assert.True(checkedFrames >= 5, "输出 MP3 的有效音频帧不足。");
    }

    private static byte[] AudioPayload(byte[] file, NcmAudioFormat format)
    {
        if (format == NcmAudioFormat.Mp3)
        {
            var start = 0;
            if (file.AsSpan().StartsWith("ID3"u8))
            {
                start = 10 + ((file[6] & 0x7f) << 21) + ((file[7] & 0x7f) << 14) +
                    ((file[8] & 0x7f) << 7) + (file[9] & 0x7f);
                if ((file[5] & 0x10) != 0)
                {
                    start += 10;
                }
            }

            var end = file.Length;
            if (end >= 128 && file.AsSpan(end - 128, 3).SequenceEqual("TAG"u8))
            {
                end -= 128;
            }

            return file[start..end];
        }

        Assert.True(file.AsSpan().StartsWith("fLaC"u8));
        var position = 4;
        while (true)
        {
            var last = (file[position] & 0x80) != 0;
            var blockLength = (file[position + 1] << 16) | (file[position + 2] << 8) | file[position + 3];
            position += 4 + blockLength;
            if (last)
            {
                return file[position..];
            }
        }
    }

    private static async Task<string> DecodePcmHashAsync(string path, string pcmFormat)
    {
        var executable = Path.Combine(
            AppContext.BaseDirectory,
            "runtimes",
            "win-x64",
            "native",
            "ffmpeg.exe");
        var start = new ProcessStartInfo(executable)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        foreach (var argument in new[]
        {
            "-v", "error", "-i", path, "-map", "0:a:0",
            "-c:a", pcmFormat, "-f", "hash", "-hash", "SHA256", "-"
        })
        {
            start.ArgumentList.Add(argument);
        }

        using var process = Process.Start(start) ?? throw new InvalidOperationException("无法启动独立 PCM 验证工具。");
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        try
        {
            var standardOutput = process.StandardOutput.ReadToEndAsync(timeout.Token);
            var standardError = process.StandardError.ReadToEndAsync(timeout.Token);
            await process.WaitForExitAsync(timeout.Token);
            var output = await standardOutput;
            var error = await standardError;
            Assert.True(process.ExitCode == 0, error);
            Assert.StartsWith("SHA256=", output.Trim());
            return output.Trim();
        }
        finally
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }
        }
    }

    private sealed class BlockingTranscoder : IAudioTranscoder
    {
        internal TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task<AudioSourceInfo> ProbeAsync(string inputPath, CancellationToken cancellationToken) =>
            Task.FromResult(new AudioSourceInfo(44100, 2, 16, TimeSpan.FromSeconds(0.5), NcmAudioFormat.Flac));

        public async Task TranscodeAsync(
            string inputPath,
            string outputPath,
            AudioTranscodePlan plan,
            CancellationToken cancellationToken)
        {
            await System.IO.File.WriteAllBytesAsync(outputPath, [1, 2, 3], cancellationToken);
            Started.SetResult();
            await Task.Delay(Timeout.Infinite, cancellationToken);
        }
    }

    private sealed class FailingTranscoder : IAudioTranscoder
    {
        public Task<AudioSourceInfo> ProbeAsync(string inputPath, CancellationToken cancellationToken) =>
            Task.FromResult(new AudioSourceInfo(44100, 2, 16, null, NcmAudioFormat.Flac));

        public async Task TranscodeAsync(
            string inputPath,
            string outputPath,
            AudioTranscodePlan plan,
            CancellationToken cancellationToken)
        {
            await System.IO.File.WriteAllBytesAsync(outputPath, [1, 2, 3], cancellationToken);
            throw new MediaExportException("模拟编码器失败。");
        }
    }

    private sealed class ShortOutputTranscoder(
        AudioExportFormat outputFormat, int sampleRate, double reportedOutputDuration, int? reportedBitrateKbps = null) : IAudioTranscoder
    {
        private int _probeCount;
        private AudioTranscodePlan? _plan;

        public Task<AudioSourceInfo> ProbeAsync(string inputPath, CancellationToken cancellationToken) =>
            Task.FromResult(Interlocked.Increment(ref _probeCount) == 1
                ? new AudioSourceInfo(sampleRate, 2, null, TimeSpan.FromSeconds(0.5), NcmAudioFormat.Mp3)
                : new AudioSourceInfo(_plan!.SampleRate, 2,
                    outputFormat == AudioExportFormat.Flac ? 16 : null,
                    TimeSpan.FromSeconds(reportedOutputDuration),
                    outputFormat == AudioExportFormat.Flac ? NcmAudioFormat.Flac : NcmAudioFormat.Mp3,
                    reportedBitrateKbps ?? _plan.Mp3BitrateKbps));

        public Task TranscodeAsync(
            string inputPath,
            string outputPath,
            AudioTranscodePlan plan,
            CancellationToken cancellationToken)
        {
            _plan = plan;
            var fixture = outputFormat == AudioExportFormat.Flac
                ? "tone-44100-stereo-s16.flac"
                : "tone-44100-stereo-mp3-128k.mp3";
            System.IO.File.Copy(Path.Combine(AppContext.BaseDirectory, "Fixtures", fixture), outputPath);
            return Task.CompletedTask;
        }
    }

    private sealed class MismatchedTranscoder(bool wrongFormat) : IAudioTranscoder
    {
        private int _probeCount;

        public Task<AudioSourceInfo> ProbeAsync(string inputPath, CancellationToken cancellationToken) =>
            Task.FromResult(Interlocked.Increment(ref _probeCount) == 1
                ? new AudioSourceInfo(44100, 2, 16, TimeSpan.FromSeconds(0.5), NcmAudioFormat.Flac)
                : new AudioSourceInfo(
                    44100,
                    2,
                    null,
                    wrongFormat ? TimeSpan.FromSeconds(0.5) : TimeSpan.FromSeconds(5),
                    wrongFormat ? NcmAudioFormat.Flac : NcmAudioFormat.Mp3,
                    192));

        public Task TranscodeAsync(
            string inputPath,
            string outputPath,
            AudioTranscodePlan plan,
            CancellationToken cancellationToken) =>
            System.IO.File.WriteAllBytesAsync(outputPath, [1, 2, 3], cancellationToken);
    }

    private sealed class TranscodeWorkspace : IDisposable
    {
        private readonly string _root = Directory.CreateTempSubdirectory().FullName;

        internal string OutputDirectory => Path.Combine(_root, "output");

        internal async Task<string> CreateInputAsync(
            NcmAudioFormat format,
            string fixtureName,
            bool withCover = false,
            bool withAudioTags = false)
        {
            byte[]? cover = null;
            if (withCover)
            {
                using var bitmap = new SKBitmap(24, 24);
                bitmap.Erase(SKColors.Purple);
                using var image = SKImage.FromBitmap(bitmap);
                using var encoded = image.Encode(SKEncodedImageFormat.Png, 100);
                cover = encoded.ToArray();
            }

            var sourceAudio = await System.IO.File.ReadAllBytesAsync(
                Path.Combine(AppContext.BaseDirectory, "Fixtures", fixtureName));
            if (withAudioTags)
            {
                var audioPath = Path.Combine(_root, "tagged-source" + Path.GetExtension(fixtureName));
                await System.IO.File.WriteAllBytesAsync(audioPath, sourceAudio);
                using (var source = TagLib.File.Create(audioPath))
                {
                    source.Tag.Title = "Source title";
                    source.Tag.Performers = ["Source artist"];
                    source.Tag.Album = "Source album";
                    source.Tag.Comment = "Keep comment";
                    source.Tag.Genres = ["Electronic"];
                    source.Tag.Year = 2024;
                    source.Tag.Track = 3;
                    using var bitmap = new SKBitmap(16, 16);
                    bitmap.Erase(SKColors.Green);
                    using var image = SKImage.FromBitmap(bitmap);
                    using var encoded = image.Encode(SKEncodedImageFormat.Png, 100);
                    source.Tag.Pictures =
                    [
                        new Picture(new ByteVector(encoded.ToArray()))
                        {
                            Type = PictureType.FrontCover,
                            MimeType = "image/png"
                        }
                    ];
                    source.Save();
                }

                sourceAudio = await System.IO.File.ReadAllBytesAsync(audioPath);
            }

            var ncm = SyntheticNcmFile.Create(
                format,
                withAudioTags ? SyntheticMetadataMode.Missing : SyntheticMetadataMode.Valid,
                cover,
                audioBytes: sourceAudio);
            var path = Path.Combine(_root, "tone.ncm");
            await System.IO.File.WriteAllBytesAsync(path, ncm.FileBytes);
            return path;
        }

        public void Dispose() => Directory.Delete(_root, recursive: true);
    }
}
