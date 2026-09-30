using Ncm.Core;
using Ncm.Media;

namespace Ncm.Media.Tests;

public sealed class AudioTranscodePlannerTests
{
    [Fact]
    public void OriginalFormatKeepsAudioParameters()
    {
        var source = new AudioSourceInfo(96000, 6, 24, TimeSpan.FromSeconds(30));

        var plan = AudioTranscodePlanner.Create(
            NcmAudioFormat.Flac,
            source,
            new MediaExportOptions());

        Assert.Equal(NcmAudioFormat.Flac, plan.OutputFormat);
        Assert.Equal(96000, plan.SampleRate);
        Assert.Equal(6, plan.Channels);
        Assert.Equal(24, plan.BitsPerSample);
        Assert.Empty(plan.Preview.Adjustments);
    }

    [Theory]
    [InlineData(128)]
    [InlineData(192)]
    [InlineData(256)]
    [InlineData(320)]
    public void Mp3PlansFixedBitrateAndNecessaryResampling(int bitrate)
    {
        var source = new AudioSourceInfo(96000, 6, 24, null);

        var plan = AudioTranscodePlanner.Create(
            NcmAudioFormat.Flac,
            source,
            new MediaExportOptions(AudioFormat: AudioExportFormat.Mp3, Mp3BitrateKbps: bitrate));

        Assert.Equal(NcmAudioFormat.Mp3, plan.OutputFormat);
        Assert.Equal(48000, plan.SampleRate);
        Assert.Equal(2, plan.Channels);
        Assert.Equal(bitrate, plan.Mp3BitrateKbps);
        Assert.Contains(plan.Preview.Adjustments, message => message.Contains("采样率"));
        Assert.Contains(plan.Preview.Adjustments, message => message.Contains("声道"));
    }

    [Theory]
    [InlineData(8000)]
    [InlineData(11025)]
    [InlineData(12000)]
    public void Mp3UsesAtLeastMpeg2SampleRateFor128Kbps(int sourceRate)
    {
        var plan = AudioTranscodePlanner.Create(
            NcmAudioFormat.Mp3,
            new AudioSourceInfo(sourceRate, 2, null, TimeSpan.FromSeconds(0.5)),
            new MediaExportOptions(AudioFormat: AudioExportFormat.Mp3, Mp3BitrateKbps: 128));

        Assert.Equal(16000, plan.SampleRate);
        Assert.Equal(128, plan.Mp3BitrateKbps);
        Assert.Contains(plan.Preview.Adjustments, message => message.Contains("采样率"));
    }

    [Fact]
    public void FlacReencodeKeepsSupportedSourceDepth()
    {
        var plan = AudioTranscodePlanner.Create(
            NcmAudioFormat.Flac,
            new AudioSourceInfo(96000, 2, 24, null),
            new MediaExportOptions(AudioFormat: AudioExportFormat.Flac, FlacCompressionLevel: 8));

        Assert.Equal(96000, plan.SampleRate);
        Assert.Equal(2, plan.Channels);
        Assert.Equal(24, plan.BitsPerSample);
        Assert.Equal(8, plan.FlacCompressionLevel);
        Assert.Empty(plan.Preview.Adjustments);
    }

    [Fact]
    public void UnsupportedFlacDepthIsAdjustedInPreview()
    {
        var plan = AudioTranscodePlanner.Create(
            NcmAudioFormat.Flac,
            new AudioSourceInfo(48000, 2, 32, null),
            new MediaExportOptions(AudioFormat: AudioExportFormat.Flac));

        Assert.Equal(24, plan.BitsPerSample);
        Assert.Contains(plan.Preview.Adjustments, message => message.Contains("位深"));
    }

    [Fact]
    public void Mp3ToFlacPreviewExplainsLossySource()
    {
        var plan = AudioTranscodePlanner.Create(
            NcmAudioFormat.Mp3,
            new AudioSourceInfo(44100, 2, null, null),
            new MediaExportOptions(AudioFormat: AudioExportFormat.Flac));

        Assert.Equal(16, plan.BitsPerSample);
        Assert.Contains(plan.Preview.Adjustments, message => message.Contains("不会恢复"));
    }

    [Theory]
    [InlineData(AudioExportFormat.Mp3, 160, 5)]
    [InlineData(AudioExportFormat.Flac, 192, 9)]
    public void UnsupportedQualityIsRejected(
        AudioExportFormat format,
        int bitrate,
        int level)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => AudioTranscodePlanner.Create(
            NcmAudioFormat.Flac,
            new AudioSourceInfo(44100, 2, 16, null),
            new MediaExportOptions(AudioFormat: format, Mp3BitrateKbps: bitrate, FlacCompressionLevel: level)));
    }
}
