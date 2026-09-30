namespace Ncm.Core;

public enum NcmAudioFormat
{
    Mp3,
    Flac
}

public sealed record NcmMetadata(
    string? Title,
    string? Album,
    IReadOnlyList<string> Artists,
    string? DeclaredFormat,
    long? AlbumId = null);

public sealed record NcmInspection(
    string SourcePath,
    NcmAudioFormat Format,
    long AudioLength,
    NcmMetadata? Metadata,
    byte[]? CoverData);

public sealed record NcmExportResult(string OutputPath, NcmInspection Inspection);

public sealed class NcmFormatException : IOException
{
    public NcmFormatException(string message) : base(message)
    {
    }

    public NcmFormatException(string message, Exception innerException) : base(message, innerException)
    {
    }
}
