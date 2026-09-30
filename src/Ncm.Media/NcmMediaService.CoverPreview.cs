using System.Text;
using Ncm.Core;
using TagLib;
using LocalFile = System.IO.File;
using TagFile = TagLib.File;

namespace Ncm.Media;

public sealed partial class NcmMediaService
{
    public async Task<bool> ValidateCoverCapacityAsync(
        string inputPath,
        MediaExportOptions options,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(options);
        ValidateOptions(options);
        if (options.CoverSelection == CoverSelection.None)
        {
            return true;
        }

        var inspection = await _core.InspectAsync(inputPath, cancellationToken);
        var outputFormat = AudioTranscodePlanner.OutputFormat(inspection.Format, options);
        byte[]? bytes;
        string? sourceFileName = null;
        if (options.CoverSelection == CoverSelection.Imported)
        {
            sourceFileName = Path.GetFullPath(options.ImportedCoverPath!);
            bytes = ReadImportedPicture(sourceFileName, cancellationToken);
        }
        else
        {
            bytes = inspection.CoverData;
            if (bytes is null)
            {
                var stageDirectory = Path.Combine(Path.GetTempPath(), $"ncm-cover-check-{Guid.NewGuid():N}");
                string? decryptedPath = null;
                try
                {
                    var exported = await _core.ExportAsync(inputPath, stageDirectory, cancellationToken);
                    decryptedPath = exported.OutputPath;
                    using var audio = TagFile.Create(decryptedPath, ReadStyle.None);
                    bytes = GetExistingPicture(audio, inspection.Format)?.Data;
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
        }

        if (bytes is null)
        {
            return false;
        }

        var cover = await Task.Run(
            () => CoverImageProcessor.Prepare(
                bytes,
                sourceFileName,
                options.CoverSelection == CoverSelection.Original ? options.OriginalCoverMaximumEdge : null,
                cancellationToken),
            cancellationToken);
        if (outputFormat == NcmAudioFormat.Flac)
        {
            MediaTagCapacity.CheckFlacPictureBlock(
                cover.Data.LongLength,
                Encoding.ASCII.GetByteCount(cover.MimeType),
                0);
        }
        else
        {
            MediaTagCapacity.CheckInputLength(outputFormat, cover.Data.LongLength);
        }

        return true;
    }
}
