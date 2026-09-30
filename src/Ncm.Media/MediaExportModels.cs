using Ncm.Core;

namespace Ncm.Media;

public enum CoverSelection
{
    Original,
    Imported,
    None
}

public enum CoverOrigin
{
    Ncm,
    Audio,
    Imported,
    None
}

public enum AudioExportFormat
{
    Original,
    Mp3,
    Flac
}

public sealed record MediaExportOptions(
    CoverSelection CoverSelection = CoverSelection.Original,
    string? ImportedCoverPath = null,
    int? OriginalCoverMaximumEdge = null,
    AudioExportFormat AudioFormat = AudioExportFormat.Original,
    int Mp3BitrateKbps = 192,
    int FlacCompressionLevel = 5);

public sealed record MediaExportResult(
    string OutputPath,
    NcmInspection Inspection,
    string Title,
    IReadOnlyList<string> Artists,
    string Album,
    CoverOrigin CoverOrigin,
    bool OriginalCoverUnavailable,
    NcmAudioFormat OutputFormat);

public sealed record AudioExportPreview(
    NcmAudioFormat SourceFormat,
    NcmAudioFormat OutputFormat,
    int SourceSampleRate,
    int OutputSampleRate,
    int SourceChannels,
    int OutputChannels,
    int? SourceBitsPerSample,
    int? OutputBitsPerSample,
    TimeSpan? Duration,
    IReadOnlyList<string> Adjustments);

public sealed class MediaExportException : IOException
{
    public MediaExportException(string message) : base(message)
    {
    }

    public MediaExportException(string message, Exception innerException) : base(message, innerException)
    {
    }
}
