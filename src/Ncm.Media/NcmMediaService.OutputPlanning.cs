using System.Buffers;
using System.Text;
using System.Text.Json;
using Ncm.Core;
using TagLib;
using LocalFile = System.IO.File;

namespace Ncm.Media;

public sealed partial class NcmMediaService
{
    public async Task<OutputPlanItem> InspectOutputItemAsync(
        string inputPath,
        MediaExportOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        options ??= new MediaExportOptions();
        ValidateOptions(options);
        var stageDirectory = Path.Combine(Path.GetTempPath(), $"ncm-plan-{Guid.NewGuid():N}");
        string? decryptedPath = null;
        try
        {
            var source = await _core.ExportAsync(inputPath, stageDirectory, cancellationToken);
            decryptedPath = source.OutputPath;
            cancellationToken.ThrowIfCancellationRequested();
            var inspection = source.Inspection;
            using var audio = OpenTagFile(decryptedPath, inspection.Format);
            var title = FirstText(inspection.Metadata?.Title, audio.Tag.Title,
                Path.GetFileNameWithoutExtension(inspection.SourcePath));
            var artists = SelectArtists(inspection.Metadata?.Artists, audio.Tag.Performers);
            var album = FirstText(inspection.Metadata?.Album, audio.Tag.Album);
            return new OutputPlanItem(
                inspection.SourcePath,
                AudioTranscodePlanner.OutputFormat(inspection.Format, options),
                title,
                artists,
                album,
                audio.Tag.Disc > 0 ? audio.Tag.Disc : null,
                audio.Tag.Track > 0 ? audio.Tag.Track : null,
                AlbumIdentity: GetAlbumIdentity(inspection.Metadata, album, audio.Tag.AlbumArtists));
        }
        catch (Exception exception) when (exception is CorruptFileException or UnsupportedFormatException)
        {
            throw new MediaExportException("读取源音频标签失败，无法规划输出。", exception);
        }
        finally
        {
            if (decryptedPath is not null && LocalFile.Exists(decryptedPath))
            {
                LocalFile.Delete(decryptedPath);
            }

            if (Directory.Exists(stageDirectory))
            {
                Directory.Delete(stageDirectory);
            }
        }
    }

    private static string? GetAlbumIdentity(NcmMetadata? metadata, string? album, string[] albumArtists)
    {
        if (metadata?.AlbumId is { } albumId)
        {
            return "ncm:" + albumId.ToString(System.Globalization.CultureInfo.InvariantCulture);
        }

        var names = albumArtists.Where(static name => !string.IsNullOrWhiteSpace(name))
            .Select(static name => name.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Order(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        if (names.Length == 0)
        {
            return null;
        }

        var buffer = new ArrayBufferWriter<byte>();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartArray();
            writer.WriteStringValue(album ?? string.Empty);
            foreach (var name in names)
            {
                writer.WriteStringValue(name);
            }

            writer.WriteEndArray();
        }

        return "tags:" + Encoding.UTF8.GetString(buffer.WrittenSpan);
    }

    public async Task<OutputPlan> PlanOutputsAsync(
        IReadOnlyList<string> inputPaths,
        string outputDirectory,
        OutputPlanOptions? planOptions = null,
        MediaExportOptions? mediaOptions = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(inputPaths);
        var items = new List<OutputPlanItem>(inputPaths.Count);
        foreach (var inputPath in inputPaths)
        {
            cancellationToken.ThrowIfCancellationRequested();
            items.Add(await InspectOutputItemAsync(inputPath, mediaOptions, cancellationToken));
        }

        return OutputPlanner.Create(outputDirectory, items, planOptions);
    }

    public async Task<MediaExportResult> ExportPlannedAsync(
        OutputPlan plan,
        PlannedOutputItem item,
        MediaExportOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentNullException.ThrowIfNull(item);
        if (!plan.Items.Contains(item))
        {
            throw new ArgumentException("目标条目不在输出规划中。", nameof(item));
        }

        options ??= new MediaExportOptions();
        ValidateOptions(options);
        cancellationToken.ThrowIfCancellationRequested();
        Directory.CreateDirectory(plan.RootPath);
        var stageDirectory = Path.Combine(plan.RootPath, $".ncm-stage-{Guid.NewGuid():N}");
        var extension = item.Source.OutputFormat == NcmAudioFormat.Mp3 ? ".mp3" : ".flac";
        var temporaryPath = Path.Combine(plan.RootPath, $".ncm-{Guid.NewGuid():N}.tmp{extension}");
        string? decryptedPath = null;
        try
        {
            var source = await _core.ExportAsync(item.Source.InputPath, stageDirectory, cancellationToken);
            decryptedPath = source.OutputPath;
            var actualFormat = AudioTranscodePlanner.OutputFormat(source.Inspection.Format, options);
            if (actualFormat != item.Source.OutputFormat)
            {
                throw new MediaExportException("输出格式与路径预览不一致，请重新规划。");
            }

            if (options.AudioFormat == AudioExportFormat.Original)
            {
                LocalFile.Move(decryptedPath, temporaryPath, overwrite: false);
                decryptedPath = temporaryPath;
            }
            else
            {
                var sourceInfo = await _transcoder.ProbeAsync(decryptedPath, cancellationToken);
                if (sourceInfo.CodecFormat != source.Inspection.Format)
                {
                    throw new MediaExportException("源音频格式与 NCM 内容识别结果不一致。");
                }

                var transcodePlan = AudioTranscodePlanner.Create(source.Inspection.Format, sourceInfo, options);
                await _transcoder.TranscodeAsync(decryptedPath, temporaryPath, transcodePlan, cancellationToken);
                cancellationToken.ThrowIfCancellationRequested();
                var actual = await _transcoder.ProbeAsync(temporaryPath, cancellationToken);
                ValidateTranscodedResult(source.Inspection.Format, sourceInfo, transcodePlan, actual);
            }

            var outcome = await Task.Run(() => ApplyTags(
                temporaryPath,
                decryptedPath,
                source.Inspection,
                actualFormat,
                options,
                cancellationToken,
                item), cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            var committed = OutputCommitter.Commit(temporaryPath, plan, item, cancellationToken);
            return new MediaExportResult(
                committed.OutputPath,
                source.Inspection,
                outcome.Title,
                outcome.Artists,
                outcome.Album,
                outcome.CoverOrigin,
                outcome.OriginalCoverUnavailable,
                actualFormat);
        }
        finally
        {
            if (LocalFile.Exists(temporaryPath))
            {
                LocalFile.Delete(temporaryPath);
            }

            if (decryptedPath is not null && decryptedPath != temporaryPath && LocalFile.Exists(decryptedPath))
            {
                LocalFile.Delete(decryptedPath);
            }

            if (Directory.Exists(stageDirectory))
            {
                Directory.Delete(stageDirectory);
            }
        }
    }

    private static void ValidateTranscodedResult(
        NcmAudioFormat sourceFormat,
        AudioSourceInfo sourceInfo,
        AudioTranscodePlan plan,
        AudioSourceInfo actual)
    {
        if (actual.CodecFormat != plan.OutputFormat ||
            actual.SampleRate != plan.SampleRate ||
            actual.Channels != plan.Channels ||
            (plan.OutputFormat == NcmAudioFormat.Flac && actual.BitsPerSample != plan.BitsPerSample))
        {
            throw new MediaExportException("转码结果的格式、采样率、声道或位深与预览不一致。");
        }

        if (plan.OutputFormat == NcmAudioFormat.Mp3 && actual.BitrateKbps != plan.Mp3BitrateKbps)
        {
            throw new MediaExportException("转码结果的 MP3 码率与所选质量不一致。");
        }

        if (sourceInfo.Duration is { } sourceDuration && sourceDuration > TimeSpan.Zero)
        {
            if (actual.Duration is not { } outputDuration || outputDuration <= TimeSpan.Zero)
            {
                throw new MediaExportException("转码结果时长与源音频不一致。");
            }

            var durationToleranceSeconds = sourceFormat == NcmAudioFormat.Mp3
                ? Math.Max(1.0, Math.Min(10.0, sourceDuration.TotalSeconds * 0.01))
                : Math.Max(0.25, Math.Min(1.0, sourceDuration.TotalSeconds * 0.001));
            var framePaddingTolerance = outputDuration < sourceDuration
                ? sourceFormat == NcmAudioFormat.Mp3 ? Mp3PaddingSeconds(sourceInfo.SampleRate) : 0
                : plan.OutputFormat == NcmAudioFormat.Mp3 ? Mp3PaddingSeconds(plan.SampleRate) : 0;
            durationToleranceSeconds = Math.Min(durationToleranceSeconds,
                Math.Max(0.001, Math.Max(framePaddingTolerance, sourceDuration.TotalSeconds * 0.1)));
            if (Math.Abs((outputDuration - sourceDuration).TotalSeconds) > durationToleranceSeconds)
            {
                throw new MediaExportException("转码结果时长与源音频不一致。");
            }
        }
    }

    private static double Mp3PaddingSeconds(int sampleRate) => 3.0 * (sampleRate >= 32000 ? 1152 : 576) / sampleRate;
}
