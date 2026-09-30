using Ncm.Core;
using Ncm.Media;

namespace Ncm.Media.Tests;

public sealed class MediaTagCapacityTests
{
    [Theory]
    [InlineData(false, 10)]
    [InlineData(true, 20)]
    public void Id3BodyChecksExactBoundary(bool hasFooter, int headerLength)
    {
        MediaTagCapacity.CheckId3TagBody(
            MediaTagCapacity.MaximumId3TagBodyLength + headerLength,
            hasFooter);

        Assert.Throws<MediaExportException>(() => MediaTagCapacity.CheckId3TagBody(
            MediaTagCapacity.MaximumId3TagBodyLength + headerLength + 1,
            hasFooter));
    }

    [Fact]
    public void FlacPictureChecksHeaderAndMimeAtBoundary()
    {
        const int mimeLength = 10;
        var maximumImageLength = MediaTagCapacity.MaximumFlacMetadataBlockLength - 32 - mimeLength;

        Assert.Equal(
            MediaTagCapacity.MaximumFlacMetadataBlockLength,
            MediaTagCapacity.FlacPictureBlockLength(maximumImageLength, mimeLength, 0));
        MediaTagCapacity.CheckFlacPictureBlock(maximumImageLength, mimeLength, 0);
        Assert.Throws<MediaExportException>(() =>
            MediaTagCapacity.CheckFlacPictureBlock(maximumImageLength + 1, mimeLength, 0));
    }

    [Fact]
    public void InputLengthUsesFinalFormatLimit()
    {
        MediaTagCapacity.CheckInputLength(NcmAudioFormat.Mp3, MediaTagCapacity.MaximumId3TagBodyLength);
        MediaTagCapacity.CheckInputLength(NcmAudioFormat.Flac, MediaTagCapacity.MaximumFlacMetadataBlockLength);

        Assert.Throws<MediaExportException>(() => MediaTagCapacity.CheckInputLength(
            NcmAudioFormat.Mp3,
            MediaTagCapacity.MaximumId3TagBodyLength + 1));
        Assert.Throws<MediaExportException>(() => MediaTagCapacity.CheckInputLength(
            NcmAudioFormat.Flac,
            MediaTagCapacity.MaximumFlacMetadataBlockLength + 1));
    }
}
