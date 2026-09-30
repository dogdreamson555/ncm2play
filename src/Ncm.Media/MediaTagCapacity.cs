using Ncm.Core;

namespace Ncm.Media;

internal static class MediaTagCapacity
{
    internal const long MaximumId3TagBodyLength = (1L << 28) - 1;
    internal const long MaximumFlacMetadataBlockLength = (1L << 24) - 1;

    internal static void CheckInputLength(NcmAudioFormat format, long byteLength)
    {
        var limit = format == NcmAudioFormat.Flac
            ? MaximumFlacMetadataBlockLength
            : MaximumId3TagBodyLength;
        if (byteLength <= 0 || byteLength > limit)
        {
            throw new MediaExportException("封面文件大小超出目标音频格式可用的标签容量。");
        }
    }

    internal static long FlacPictureBlockLength(long imageLength, int mimeLength, int descriptionLength) =>
        checked(32L + imageLength + mimeLength + descriptionLength);

    internal static void CheckFlacPictureBlock(long imageLength, int mimeLength, int descriptionLength)
    {
        if (FlacPictureBlockLength(imageLength, mimeLength, descriptionLength) > MaximumFlacMetadataBlockLength)
        {
            throw new MediaExportException("FLAC PICTURE 块超过 24 位长度上限，请更换或调整封面。");
        }
    }

    internal static void CheckId3TagBody(long renderedTagLength, bool hasFooter)
    {
        var headerLength = hasFooter ? 20 : 10;
        if (renderedTagLength < headerLength ||
            renderedTagLength - headerLength > MaximumId3TagBodyLength)
        {
            throw new MediaExportException("MP3 ID3v2 标签超过 28 位长度上限，请更换或调整封面。");
        }
    }
}
