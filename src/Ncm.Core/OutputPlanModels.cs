namespace Ncm.Core;

public enum OutputClassification
{
    None,
    Album,
    Artist
}

public enum OutputFileNaming
{
    SourceFileName,
    ArtistTitle,
    TitleArtist
}

public enum OutputOrderBasis
{
    InputOrder,
    SourceTrack,
    NaturalFileName,
    Manual
}

public enum OutputTrackNumbering
{
    Automatic,
    AlbumOrder,
    ListOrder
}

public sealed record OutputPlanOptions(
    OutputClassification Classification = OutputClassification.None,
    OutputFileNaming FileNaming = OutputFileNaming.SourceFileName,
    bool NumberTracks = false,
    OutputTrackNumbering TrackNumbering = OutputTrackNumbering.Automatic);

public sealed record OutputPlanItem(
    string InputPath,
    NcmAudioFormat OutputFormat,
    string? Title,
    IReadOnlyList<string>? Artists,
    string? Album,
    uint? DiscNumber = null,
    uint? TrackNumber = null,
    string? GroupOverride = null,
    int? ManualOrder = null,
    string? AlbumIdentity = null);

public sealed record PlannedOutputItem(
    OutputPlanItem Source,
    string GroupName,
    string Title,
    IReadOnlyList<string> Artists,
    string Album,
    string TargetDirectory,
    string RequestedFileName,
    string FileName,
    string TargetPath,
    uint? WrittenDiscNumber,
    uint? WrittenTrackNumber,
    int OrderInGroup,
    OutputOrderBasis OrderBasis,
    bool NameWasShortened);

public sealed record OutputPlan(
    string RootPath,
    OutputPlanOptions Options,
    IReadOnlyList<PlannedOutputItem> Items);

public sealed record OutputFileNamePreview(
    string InputPath,
    string FileName,
    uint? WrittenDiscNumber,
    uint? WrittenTrackNumber,
    OutputOrderBasis OrderBasis);

public sealed record OutputCommitResult(string OutputPath, int CollisionIndex);
