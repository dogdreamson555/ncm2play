using Ncm.Core;

namespace Ncm.Media;

internal sealed record AudioSourceInfo(
    int SampleRate,
    int Channels,
    int? BitsPerSample,
    TimeSpan? Duration,
    NcmAudioFormat? CodecFormat = null,
    int? BitrateKbps = null);

internal sealed record AudioTranscodePlan(
    NcmAudioFormat OutputFormat,
    int SampleRate,
    int Channels,
    int? BitsPerSample,
    int? Mp3BitrateKbps,
    int? FlacCompressionLevel,
    AudioExportPreview Preview);

internal static class AudioTranscodePlanner
{
    private static readonly int[] Mp3SampleRates =
        [16000, 22050, 24000, 32000, 44100, 48000];

    private static readonly int[] HighBitrateMp3SampleRates = [32000, 44100, 48000];

    internal static NcmAudioFormat OutputFormat(NcmAudioFormat sourceFormat, MediaExportOptions options) =>
        options.AudioFormat switch
        {
            AudioExportFormat.Original => sourceFormat,
            AudioExportFormat.Mp3 => NcmAudioFormat.Mp3,
            AudioExportFormat.Flac => NcmAudioFormat.Flac,
            _ => throw new ArgumentOutOfRangeException(nameof(options), "未知的目标音频格式。")
        };

    internal static AudioTranscodePlan Create(
        NcmAudioFormat sourceFormat,
        AudioSourceInfo source,
        MediaExportOptions options)
    {
        if (source.SampleRate <= 0 || source.Channels <= 0)
        {
            throw new MediaExportException("无法读取源音频的采样率或声道数。");
        }

        var outputFormat = OutputFormat(sourceFormat, options);
        var adjustments = new List<string>();
        int sampleRate;
        int channels;
        int? bitsPerSample;
        int? mp3Bitrate = null;
        int? flacLevel = null;

        if (options.AudioFormat == AudioExportFormat.Mp3)
        {
            if (options.Mp3BitrateKbps is not (128 or 192 or 256 or 320))
            {
                throw new ArgumentOutOfRangeException(nameof(options), "MP3 固定码率仅支持 128、192、256 或 320 kbps。");
            }

            var supportedRates = options.Mp3BitrateKbps > 160
                ? HighBitrateMp3SampleRates
                : Mp3SampleRates;
            sampleRate = supportedRates.MinBy(rate => Math.Abs((long)rate - source.SampleRate));
            channels = Math.Min(source.Channels, 2);
            bitsPerSample = null;
            mp3Bitrate = options.Mp3BitrateKbps;
        }
        else if (options.AudioFormat == AudioExportFormat.Flac)
        {
            if (options.FlacCompressionLevel is < 0 or > 8)
            {
                throw new ArgumentOutOfRangeException(nameof(options), "FLAC 压缩级别仅支持 0 到 8。");
            }

            sampleRate = source.SampleRate;
            channels = source.Channels <= 8 ? source.Channels : 2;
            bitsPerSample = sourceFormat == NcmAudioFormat.Flac && source.BitsPerSample is > 16
                ? 24
                : 16;
            flacLevel = options.FlacCompressionLevel;
            if (sourceFormat == NcmAudioFormat.Mp3)
            {
                adjustments.Add("MP3 已有损压缩，转为 FLAC 不会恢复原有音质。");
            }
        }
        else
        {
            sampleRate = source.SampleRate;
            channels = source.Channels;
            bitsPerSample = source.BitsPerSample;
        }

        if (sampleRate != source.SampleRate)
        {
            adjustments.Add($"采样率需从 {source.SampleRate} Hz 调整为 {sampleRate} Hz。");
        }

        if (channels != source.Channels)
        {
            adjustments.Add($"声道数需从 {source.Channels} 调整为 {channels}。");
        }

        if (outputFormat == NcmAudioFormat.Flac &&
            sourceFormat == NcmAudioFormat.Flac &&
            source.BitsPerSample is { } sourceBits &&
            sourceBits != bitsPerSample)
        {
            adjustments.Add($"位深需从 {sourceBits} bit 调整为 {bitsPerSample} bit。");
        }

        var preview = new AudioExportPreview(
            sourceFormat,
            outputFormat,
            source.SampleRate,
            sampleRate,
            source.Channels,
            channels,
            source.BitsPerSample,
            bitsPerSample,
            source.Duration,
            adjustments);
        return new AudioTranscodePlan(
            outputFormat,
            sampleRate,
            channels,
            bitsPerSample,
            mp3Bitrate,
            flacLevel,
            preview);
    }
}
