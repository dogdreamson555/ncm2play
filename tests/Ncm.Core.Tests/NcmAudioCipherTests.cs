using System.Text;
using Ncm.Core;

namespace Ncm.Core.Tests;

public sealed class NcmAudioCipherTests
{
    private static readonly byte[] AudioKey = Encoding.ASCII.GetBytes("stream-boundary-key");

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(32767)]
    [InlineData(32768)]
    [InlineData(32769)]
    public async Task CopyAsyncPreservesAudioAtChunkBoundaries(int length)
    {
        var expected = CreateDeterministicBytes(length);
        var encrypted = SyntheticNcmFile.EncryptAudioPayload(expected, AudioKey)
            .Concat(new byte[] { 0x91, 0x92, 0x93 })
            .ToArray();
        using var input = new ChunkLimitedReadStream(encrypted, 1379);
        using var output = new TrackingWriteStream();

        await NcmAudioCipher.CopyAsync(input, output, AudioKey, length);

        Assert.Equal(expected, output.ToArray());
        Assert.Equal((long)length, input.TotalBytesRead);
        Assert.Equal((long)length, output.TotalBytesWritten);
        Assert.Equal((long)length, input.Position);
    }

    [Fact]
    public async Task CopyAsyncKeepsReadAndWriteChunksBoundedForLargeAudio()
    {
        const int bufferLimit = 32768;
        const int length = 4 * 1024 * 1024 + 123;
        var expected = CreateDeterministicBytes(length);
        var encrypted = SyntheticNcmFile.EncryptAudioPayload(expected, AudioKey);
        using var input = new ChunkLimitedReadStream(encrypted, 8191);
        using var output = new TrackingWriteStream();

        await NcmAudioCipher.CopyAsync(input, output, AudioKey, length);

        Assert.Equal(expected, output.ToArray());
        Assert.Equal((long)length, input.TotalBytesRead);
        Assert.Equal((long)length, output.TotalBytesWritten);
        Assert.InRange(input.LargestReadRequest, 1, bufferLimit);
        Assert.InRange(output.LargestWrite, 1, bufferLimit);
    }

    private static byte[] CreateDeterministicBytes(int length)
    {
        var bytes = new byte[length];
        for (var i = 0; i < bytes.Length; i++)
        {
            bytes[i] = (byte)(i * 37 + 19);
        }

        return bytes;
    }

    private sealed class ChunkLimitedReadStream(byte[] bytes, int maxReadSize) : Stream
    {
        private readonly MemoryStream _inner = new(bytes, writable: false);

        public long TotalBytesRead { get; private set; }
        public int LargestReadRequest { get; private set; }
        public override bool CanRead => true;
        public override bool CanSeek => true;
        public override bool CanWrite => false;
        public override long Length => _inner.Length;
        public override long Position
        {
            get => _inner.Position;
            set => _inner.Position = value;
        }

        public override int Read(byte[] buffer, int offset, int count)
        {
            LargestReadRequest = Math.Max(LargestReadRequest, count);
            var read = _inner.Read(buffer, offset, Math.Min(count, maxReadSize));
            TotalBytesRead += read;
            return read;
        }

        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            LargestReadRequest = Math.Max(LargestReadRequest, buffer.Length);
            var allowed = buffer[..Math.Min(buffer.Length, maxReadSize)];
            return ReadAndCountAsync(allowed, cancellationToken);
        }

        private async ValueTask<int> ReadAndCountAsync(Memory<byte> buffer, CancellationToken cancellationToken)
        {
            var read = await _inner.ReadAsync(buffer, cancellationToken);
            TotalBytesRead += read;
            return read;
        }

        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
            ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => _inner.Seek(offset, origin);
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                _inner.Dispose();
            }

            base.Dispose(disposing);
        }
    }

    private sealed class TrackingWriteStream : Stream
    {
        private readonly MemoryStream _inner = new();

        public long TotalBytesWritten { get; private set; }
        public int LargestWrite { get; private set; }
        public override bool CanRead => false;
        public override bool CanSeek => true;
        public override bool CanWrite => true;
        public override long Length => _inner.Length;
        public override long Position
        {
            get => _inner.Position;
            set => _inner.Position = value;
        }

        public byte[] ToArray() => _inner.ToArray();

        public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
        {
            LargestWrite = Math.Max(LargestWrite, buffer.Length);
            TotalBytesWritten += buffer.Length;
            return _inner.WriteAsync(buffer, cancellationToken);
        }

        public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
            WriteAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

        public override void Write(byte[] buffer, int offset, int count)
        {
            LargestWrite = Math.Max(LargestWrite, count);
            TotalBytesWritten += count;
            _inner.Write(buffer, offset, count);
        }

        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override void Flush() => _inner.Flush();
        public override long Seek(long offset, SeekOrigin origin) => _inner.Seek(offset, origin);
        public override void SetLength(long value) => _inner.SetLength(value);

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                _inner.Dispose();
            }

            base.Dispose(disposing);
        }
    }
}
