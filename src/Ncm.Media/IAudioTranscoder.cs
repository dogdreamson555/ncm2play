namespace Ncm.Media;

internal interface IAudioTranscoder
{
    Task<AudioSourceInfo> ProbeAsync(string inputPath, CancellationToken cancellationToken);

    Task TranscodeAsync(
        string inputPath,
        string outputPath,
        AudioTranscodePlan plan,
        CancellationToken cancellationToken);
}
